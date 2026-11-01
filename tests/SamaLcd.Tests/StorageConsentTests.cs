using SamaLcd.Core;

namespace SamaLcd.Tests;
public class StorageConsentTests
{
    [Fact] public void SingleDeletionOnlyTargetsTheSelectedFileFromTheApprovedList()
    {
        var card = new SdCardState("snapshot",[new("one.mp4",10),new("two.mp4",20)],false,null);
        var selected = VideoStoragePolicy.SelectSnapshot(card,"snapshot","two.mp4");
        var remaining = card.Files!.ToDictionary(f=>f.Name,f=>f.Size);
        VideoStoragePolicy.DeleteApprovedSnapshot(true,selected,n=>remaining.GetValueOrDefault(n),n=>remaining.Remove(n));
        Assert.Single(remaining); Assert.Equal(10,remaining["one.mp4"]);
    }
    [Theory]
    [InlineData("old","one.mp4",false)]
    [InlineData("snapshot","missing.mp4",false)]
    [InlineData("snapshot","../one.mp4",false)]
    [InlineData("snapshot","one.mp4",true)]
    public void SingleDeletionRejectsStaleUnknownOrBusyRequests(string revision,string name,bool busy)
    {
        var card = new SdCardState("snapshot",[new("one.mp4",10)],busy,null);
        Assert.Throws<IOException>(()=>VideoStoragePolicy.SelectSnapshot(card,revision,name));
    }
    [Fact] public void FullCardStillAllowsConversionBeforeConsent()
    {
        long[] storage=[2688,2688,0,122882,122882,0];
        Assert.Equal(122882L*1024-VideoStoragePolicy.ReserveBytes,VideoStoragePolicy.EncodingBudget(storage,122));
    }
    [Fact] public void FreeSpaceIsUsedWhenItCanFitTheVideo()
    {
        Assert.Equal((10056L-512)*1024,VideoStoragePolicy.EncodingBudget([2688,184,2504,122882,112826,10056],122));
    }
    [Fact] public void DecliningConsentDoesNotReadOrDeleteFiles()
    {
        int calls=0;
        Assert.Throws<OperationCanceledException>(()=>VideoStoragePolicy.DeleteApprovedSnapshot(false,[new("old.mp4",100)],_=>{calls++;return 100;},_=>calls++));
        Assert.Equal(0,calls);
    }
    [Fact] public void ApprovalOnlyDeletesListedSnapshot()
    {
        var files=new Dictionary<string,long>{{"one.mp4",10},{"two.mp4",20},{"new.mp4",30}};
        VideoStoragePolicy.DeleteApprovedSnapshot(true,[new("one.mp4",10),new("two.mp4",20)],n=>files.GetValueOrDefault(n),n=>files.Remove(n));
        Assert.Single(files);Assert.Equal(30,files["new.mp4"]);
    }
    [Fact] public void ChangedSnapshotPreventsAllDeletion()
    {
        int deleted=0;
        Assert.Throws<IOException>(()=>VideoStoragePolicy.DeleteApprovedSnapshot(true,[new("one.mp4",10),new("two.mp4",20)],n=>n=="one.mp4"?10:21,_=>deleted++));
        Assert.Equal(0,deleted);
    }
    [Fact] public void FailedDeletionIsDetected()
    {
        Assert.Throws<IOException>(()=>VideoStoragePolicy.DeleteApprovedSnapshot(true,[new("one.mp4",10)],_=>10,_=>{}));
    }
    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../other.mp4")]
    [InlineData("folder/file.mp4")]
    [InlineData("folder\\file.mp4")]
    public void UnsafeNamesCannotEscapeTheDisplayedVideoDirectory(string name)
    {
        int deleted=0;
        Assert.Throws<IOException>(()=>VideoStoragePolicy.DeleteApprovedSnapshot(true,[new(name,10)],_=>10,_=>deleted++));
        Assert.Equal(0,deleted);
    }
}
