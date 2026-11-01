using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;
public class VideoBudgetTests
{
    [Fact] public void BitrateFitsTwoMinuteVideoInAvailableStorage()
    {
        long budget=(10056L-512)*1024;
        int bitrate=DeviceVideo.TargetBitrate(budget,122.136961);
        Assert.InRange(bitrate,500,600);
        Assert.True(bitrate*1000*122.136961/8 < budget*.91);
    }
    [Fact] public void QualityIsCappedAndImpossibleBudgetFails()
    {
        Assert.Equal(2400,DeviceVideo.TargetBitrate(100_000_000,10));
        Assert.Throws<IOException>(()=>DeviceVideo.TargetBitrate(1_000_000,3600));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DeviceVideo.TargetBitrate(1_000_000,double.NaN));
    }
}
