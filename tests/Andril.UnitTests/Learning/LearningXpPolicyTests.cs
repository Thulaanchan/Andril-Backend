using Andril.Learning.Progress;
using Xunit;
namespace Andril.UnitTests.Learning;
public sealed class LearningXpPolicyTests
{
    [Fact] public void ProductionEarningConfigurationDefaultsToUnconfigured()
    {Assert.False(LearningXpPolicy.Unconfigured.IsConfigured);Assert.Empty(LearningXpPolicy.Unconfigured.EventAmounts);Assert.Empty(LearningXpPolicy.Unconfigured.Levels);Assert.Empty(LearningXpPolicy.Unconfigured.Achievements);Assert.Null(LearningXpPolicy.Unconfigured.MaximumTotal);}
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void UnapprovedOrInvalidAmountsNeverBecomeConfigured(long amount)
    {var p=new LearningXpPolicy("synthetic-only",true,new Dictionary<string,long>{{"synthetic-event",amount}},null,new Dictionary<string,int>(),[],[]);Assert.False(p.IsConfigured);Assert.False((p with{Approved=false}).IsConfigured);}
    [Fact] public void VersionedSyntheticCapsAndThresholdsDoNotDefineProductionEvents()
    {
        var p=new LearningXpPolicy("synthetic-only-v1",true,new Dictionary<string,long>{{"synthetic-event",7}},20,new Dictionary<string,int>{{"synthetic-event",2}},[new("synthetic-level",7)],[new("synthetic-achievement",14)]);
        Assert.True(p.IsConfigured);Assert.False((p with{Revision=""}).IsConfigured);Assert.False((p with{MaximumTotal=0}).IsConfigured);
        Assert.False((p with{MaximumOccurrences=new Dictionary<string,int>{{"unapproved-event",1}}}).IsConfigured);
        Assert.False((p with{Levels=[new("duplicate",1),new("duplicate",2)]}).IsConfigured);
    }
}
