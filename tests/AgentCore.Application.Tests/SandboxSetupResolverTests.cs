using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;
public sealed class SandboxSetupResolverTests
{
    private static readonly SandboxProfile Profile = new("workbench",1,"Workbench","",true,"sha256:"+new string('a',64),"echo Profile");
    [Fact]
    public void Sparse_setup_supplements_scripts_limits_resources_and_keeps_profile_revision()
    {
        var fields=InstanceSettingsResolver.Patch(new(),"sandboxPolicy",new Dictionary<string,JsonElement>{
            ["initializationScript"]=JsonSerializer.SerializeToElement("echo Instance"),["cpu"]=JsonSerializer.SerializeToElement(.5),["memoryMiB"]=JsonSerializer.SerializeToElement(512)},[]);
        var setup=SandboxSetupResolver.Resolve(Profile,fields,7,rejectExpansion:true);
        Assert.Equal("echo Profile",setup.InitializationScript);Assert.Equal("echo Instance",setup.InstanceInitializationScript);
        Assert.Equal(1,setup.Revision);Assert.Equal(7,setup.InstanceRevision);Assert.Equal(.5,setup.Cpu);Assert.Equal(512,setup.MemoryMiB);
        Assert.Equal(1800,setup.IdleTtlSeconds);Assert.Equal(7200,setup.MaxLifetimeSeconds);
        fields=InstanceSettingsResolver.Patch(fields,"sandboxPolicy",new Dictionary<string,JsonElement>(),["cpu","memoryMiB","initializationScript"]);
        Assert.Equal(Profile.Cpu,SandboxSetupResolver.Resolve(Profile,fields,8).Cpu);Assert.Null(SandboxSetupResolver.Resolve(Profile,fields,8).InstanceInitializationScript);
    }
    [Fact]
    public void Expansion_invalid_setup_and_unauthorized_profile_selection_are_rejected()
    {
        Assert.Throws<AgentCoreException>(()=>SandboxSetupResolver.Resolve(Profile,new(SandboxCpu:new(2)),1,rejectExpansion:true));
        Assert.Throws<AgentCoreException>(()=>SandboxSetupResolver.Resolve(Profile,new(SandboxInitializationScript:new(new string('x',17000))),1));
        Assert.Throws<AgentCoreException>(()=>SandboxSetupResolver.Resolve(Profile,new(SandboxIdleTtlSeconds:new(300),SandboxMaxLifetimeSeconds:new(60)),1,rejectExpansion:true));
        var definition=SampleDefinitions.Examiner with {SandboxPolicy=new("workbench",["workbench"])};
        Assert.Throws<AgentCoreException>(()=>InstanceSettingsResolver.Resolve(definition,new(SandboxProfileId:new("unauthorized"))));
        // Later Global ceiling reductions narrow existing saved requests without expanding execution authority.
        Assert.Equal(.5,SandboxSetupResolver.Resolve(Profile with {Cpu=.5},new(SandboxCpu:new(1)),2).Cpu);
    }
    [Fact]
    public void Host_validation_works_before_profile_configuration_and_inherited_idle_stays_inside_saved_maximum_lifetime()
    {
        Assert.Throws<AgentCoreException>(()=>InstanceSettingsResolver.Resolve(SampleDefinitions.Examiner,new(SandboxCpu:new(3))));
        Assert.Throws<AgentCoreException>(()=>InstanceSettingsResolver.Resolve(SampleDefinitions.Examiner,new(SandboxInitializationScript:new("bad\0text"))));
        var saved=new InstanceSettingsOverrides(SandboxMaxLifetimeSeconds:new(2400));
        var changed=Profile with { IdleTtlSeconds=3600 };
        Assert.Equal(2400,SandboxSetupResolver.Resolve(changed,saved,3).IdleTtlSeconds);
        Assert.Throws<AgentCoreException>(()=>SandboxSetupResolver.Resolve(changed,saved,3,rejectExpansion:true));
    }

    [Fact]
    public void Allowed_subset_is_sparse_exact_and_cannot_exclude_the_effective_default()
    {
        var definition = SampleDefinitions.Examiner with { SandboxPolicy = new("workbench", ["workbench", "dotnet", "minimal"]) };
        var initial = InstanceSettingsResolver.Resolve(definition, new());
        Assert.Equal(definition.SandboxPolicy!.AllowedProfileIds, initial.SandboxPolicy!.AllowedProfileIds);
        var settings = InstanceSettingsResolver.Patch(new(), "sandboxPolicy", new Dictionary<string, JsonElement> {
            ["allowedProfileIds"] = JsonSerializer.SerializeToElement(new[] { "dotnet", "minimal" }),
            ["profileId"] = JsonSerializer.SerializeToElement("dotnet") }, []);
        var narrowed = InstanceSettingsResolver.Resolve(definition, settings);
        Assert.Equal(new[] { "dotnet", "minimal" }, narrowed.SandboxPolicy!.AllowedProfileIds);
        Assert.Equal("dotnet", narrowed.SandboxPolicy.DefaultProfileId);
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(definition, settings with { SandboxProfileId = null }));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(definition, settings with { SandboxAllowedProfileIds = new(["foreign", "dotnet"]) }));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(definition, settings with { SandboxAllowedProfileIds = new(["dotnet", "dotnet"]) }));
        Assert.Throws<AgentCoreException>(() => InstanceSettingsResolver.Resolve(definition, settings with { SandboxAllowedProfileIds = new([]) }));
        var reset = InstanceSettingsResolver.Patch(settings, "sandboxPolicy", new Dictionary<string, JsonElement>(), ["allowedProfileIds", "profileId"]);
        Assert.Equal(definition.SandboxPolicy!.AllowedProfileIds, InstanceSettingsResolver.Resolve(definition, reset).SandboxPolicy!.AllowedProfileIds);
        var copied = settings.Copy(); settings.SandboxAllowedProfileIds!.Value[0] = "foreign";
        Assert.Equal("dotnet", copied.SandboxAllowedProfileIds!.Value[0]);
    }

}
