using System.IO.Compression;
using System.Text.Json;
using SamaLcd.Core;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class DiagnosticLogTests
{
    [Fact]
    public void UnexpectedDecoderThreadFailureIsLoggedInsteadOfTerminatingTheProcess()
    {
        if (VideoSource.FindFfmpeg() == null) return;
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        var previous = AppLog.Current;
        try
        {
            using (var log = new DiagnosticLog(dir))
            {
                AppLog.Current = log;
                var input = Path.Combine(dir,"invalid.mp4"); File.WriteAllText(input,"not a video");
                // Reject impossible frame allocation inside the background reader, before touching GDI.
                using (var source = new VideoSource(input,int.MaxValue,int.MaxValue,ImageFit.Cover,20))
                    Assert.True(SpinWait.SpinUntil(()=>source.Error != null,3000));
            }
            AppLog.Current = previous;
            Assert.Contains(ReadRows(dir),r=>r.GetProperty("eventName").GetString()=="video.decoder_reader" && r.GetProperty("exception").GetString()!.Contains("OverflowException"));
        }
        finally { AppLog.Current = previous; Directory.Delete(dir,true); }
    }

    [Fact]
    public void ErrorsAndOperationsSurviveWithDebugOffAndTheSwitchPersists()
    {
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        try
        {
            using (var log = new DiagnosticLog(dir))
            {
                log.Write("DEBUG", "hidden");
                log.Write("INFO", "operation", new { file = "chosen.mp4" }, operationId: "chosen-operation");
                log.Write("ERROR", "failure", exception: new IOException("outer", new InvalidOperationException("inner")));
                log.DebugEnabled = true; log.Write("DEBUG", "visible"); log.CompleteSession();
            }
            var rows = ReadRows(dir);
            Assert.DoesNotContain(rows, r=>r.GetProperty("eventName").GetString()=="hidden");
            Assert.Contains(rows, r=>r.GetProperty("eventName").GetString()=="visible");
            Assert.Contains(rows, r=>r.GetProperty("operationId").GetString()=="chosen-operation");
            var error = rows.Single(r=>r.GetProperty("eventName").GetString()=="failure").GetProperty("exception").GetString()!;
            Assert.Contains("outer",error); Assert.Contains("InvalidOperationException",error); Assert.Contains("inner",error);
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { DebugLogEnabled = true }))!;
            Assert.True(restored.DebugLogEnabled);
            Assert.False(JsonSerializer.Deserialize<AppSettings>("{}")!.DebugLogEnabled);
        }
        finally { Directory.Delete(dir,true); }
    }

    [Fact]
    public void ConcurrentWritesRemainCompleteJsonLinesAndFilesRotate()
    {
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        try
        {
            using (var log = new DiagnosticLog(dir,maximumBytes:2048,retainedFiles:3))
                Parallel.For(0,100,i=>log.Write("INFO","concurrent",new { number=i }));
            Assert.InRange(Directory.GetFiles(dir,"*.log").Length,2,3);
            var rows = ReadRows(dir);
            Assert.NotEmpty(rows);
            Assert.All(rows, r=>Assert.Equal("INFO",r.GetProperty("level").GetString()));
            Assert.All(rows, r=>Assert.NotEmpty(r.GetProperty("session").GetString()!));
        }
        finally { Directory.Delete(dir,true); }
    }

    [Fact]
    public void MissingCleanExitIsDetectedAndCleanExitIsNotReportedAsACrash()
    {
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        try
        {
            using (var first = new DiagnosticLog(dir)) first.Write("INFO","before-abrupt-exit");
            using (var second = new DiagnosticLog(dir)) second.CompleteSession();
            Assert.Single(ReadRows(dir).Where(r=>r.GetProperty("eventName").GetString()=="session.previous_incomplete"));
            using (var third = new DiagnosticLog(dir)) third.CompleteSession();
            Assert.Single(ReadRows(dir).Where(r=>r.GetProperty("eventName").GetString()=="session.previous_incomplete"));
        }
        finally { Directory.Delete(dir,true); }
    }

    [Fact]
    public void ExportContainsLogsAndMetadataWithoutCopyingMedia()
    {
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        try
        {
            using var log = new DiagnosticLog(dir);
            File.WriteAllText(Path.Combine(dir,"private-video.mp4"),"never export media");
            log.Write("ERROR","exported-error",exception:new IOException("diagnostic example"));
            var archivePath = Path.Combine(dir,"diagnostics.zip");
            log.Export(archivePath,new { device="COM3",sourceName="private-video.mp4" },"Windows native crash example");
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.Contains(archive.Entries,e=>e.FullName.StartsWith("logs/"));
            Assert.NotNull(archive.GetEntry("diagnostics.json"));
            Assert.NotNull(archive.GetEntry("windows-crashes.txt"));
            Assert.DoesNotContain(archive.Entries,e=>e.Name.EndsWith(".mp4"));
            using var metadata = JsonDocument.Parse(archive.GetEntry("diagnostics.json")!.Open());
            Assert.Equal("COM3",metadata.RootElement.GetProperty("snapshot").GetProperty("device").GetString());
        }
        finally { Directory.Delete(dir,true); }
    }

    [Fact]
    public void UnwritableLogLocationDoesNotInterruptTheApplication()
    {
        var dir = Directory.CreateTempSubdirectory("samalcd-log-").FullName;
        try
        {
            var invalidDirectory = Path.Combine(dir,"a-file"); File.WriteAllText(invalidDirectory,"occupied");
            using var log = new DiagnosticLog(invalidDirectory);
            log.Write("ERROR","io-failure",exception:new IOException("example"));
            log.DebugEnabled = true; log.CompleteSession();
            Assert.NotNull(log.LastError);
        }
        finally { Directory.Delete(dir,true); }
    }

    private static JsonElement[] ReadRows(string directory) => Directory.GetFiles(directory,"*.log")
        .SelectMany(File.ReadAllLines).Select(line=>{using var doc=JsonDocument.Parse(line);return doc.RootElement.Clone();}).ToArray();
}
