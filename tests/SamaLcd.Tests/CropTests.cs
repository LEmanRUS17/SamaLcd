using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SamaLcd.Core;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class CropTests
{
    [Theory]
    [InlineData(1920,1080)]
    [InlineData(1080,1920)]
    [InlineData(480,480)]
    public void CropIsSquareAndStaysInsideSource(int w,int h)
    {
        foreach(var c in new[]{new SquareCrop(.9,.9,.5),new SquareCrop(-2,4,10),new SquareCrop(double.NaN,double.PositiveInfinity,0)})
        {
            var r=c.Rectangle(w,h);
            Assert.Equal(r.Width,r.Height);
            Assert.True(r.Left>=0 && r.Top>=0 && r.Right<=w && r.Bottom<=h);
        }
    }

    [Fact]
    public void CropPersistsAndBackgroundAppliesIt()
    {
        var s=new AppSettings {BackgroundMode=BackgroundMode.File,BackgroundFile="source.png",BackgroundCrop=new SquareCrop(.2,.1,.5)};
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s,Theme.JsonOptions),Theme.JsonOptions)!;
        Assert.Equal(s.BackgroundCrop,restored.BackgroundCrop);
        Assert.Equal(s.BackgroundCrop,DisplayEngine.WithBackground(new Theme(),restored).Background.Crop);
    }

    [Fact]
    public async Task ImageAndFfmpegSelectTheSameRegion()
    {
        var ffmpeg=VideoSource.FindFfmpeg();
        var dir=Directory.CreateTempSubdirectory("samalcd-crop-").FullName;
        try
        {
            string input=Path.Combine(dir,"source.png"),output=Path.Combine(dir,"cropped.png"),video=Path.Combine(dir,"cropped.mp4");
            using(var image=new Bitmap(800,480))
            {
                using(var g=Graphics.FromImage(image)) {g.Clear(Color.Red);g.FillRectangle(Brushes.Blue,400,0,400,480);}
                image.Save(input,ImageFormat.Png);
            }
            var crop=new SquareCrop(.6,0,.5);
            using(var renderer=new ThemeRenderer(new Theme {Background=new Background {Image=input,Crop=crop}},_=>null))
            using(var rendered=renderer.Render()) Assert.True(rendered.GetPixel(240,240).B>240);
            if(ffmpeg==null) return; // optional integration: run when FFmpeg is installed
            var psi=new ProcessStartInfo(ffmpeg!) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            foreach(var a in new[]{"-y","-loop","1","-i",input,"-vf",crop.Filter()+",scale=480:480","-t","0.2","-c:v","libx264","-pix_fmt","yuv420p",video}) psi.ArgumentList.Add(a);
            using var p=Process.Start(psi)!;
            var error=p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(30000).Token);
            Assert.True(p.ExitCode==0,await error);
            psi.ArgumentList.Clear();
            foreach(var a in new[]{"-y","-i",video,"-frames:v","1",output}) psi.ArgumentList.Add(a);
            using var decode=Process.Start(psi)!;
            var decodeError=decode.StandardError.ReadToEndAsync();
            await decode.WaitForExitAsync(new CancellationTokenSource(30000).Token);
            Assert.True(decode.ExitCode==0,await decodeError);
            using var result=new Bitmap(output);
            Assert.Equal(480,result.Width);Assert.Equal(480,result.Height);Assert.True(result.GetPixel(240,240).B>240);
        }
        finally {Directory.Delete(dir,true);}
    }

    [Fact]
    public void InstallerExtractsOnlyExecutableAndPreservesExistingFileOnBadArchive()
    {
        var dir=Directory.CreateTempSubdirectory("samalcd-zip-").FullName;
        try
        {
            var archive=Path.Combine(dir,"test.zip");var target=Path.Combine(dir,"ffmpeg.exe");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
            {
                using(var s=zip.CreateEntry("build/bin/ffmpeg.exe").Open()) s.Write(new byte[]{(byte)'M',(byte)'Z',1,2});
                using var bad=zip.CreateEntry("../escape.txt").Open();bad.WriteByte(1);
            }
            FfmpegInstaller.InstallArchive(archive,target);Assert.Equal(4,new FileInfo(target).Length);
            Assert.False(File.Exists(Path.Combine(dir,"escape.txt")));
            var empty=Path.Combine(dir,"empty.zip");using(ZipFile.Open(empty,ZipArchiveMode.Create)){}
            Assert.Throws<IOException>(()=>FfmpegInstaller.InstallArchive(empty,target));Assert.Equal(4,new FileInfo(target).Length);
        }
        finally {Directory.Delete(dir,true);}
    }
}
