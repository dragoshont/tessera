namespace Tessera.Core.Product;

public sealed record JobSchedule(string Kind, DateTimeOffset? At, TimeOnly? LocalTime, string TimeZone, DayOfWeek[]? Days);
public sealed record DevelopmentWorkspace(string OwnerPrincipalId,string WorkspaceId,string ConversationId,string DisplayName,
    string SnapshotRef,string SnapshotHash,string State,DateTimeOffset CreatedAt,long Version);
public sealed record DevelopmentJobSpec(string WorkspaceId,string CommandProfile,IReadOnlyList<string> Arguments,string Effect,
    int TimeoutSeconds,int OutputLimitBytes,string ExecutorImageDigest);
public sealed record ProductJob(string OwnerPrincipalId,string JobId,string Name,string Instruction,string DesiredState,string Health,
    string? ModelProfileId,JobSchedule Schedule,DateTimeOffset? NextOccurrence,string ContextPolicyJson,
    IReadOnlyList<string> AccountGrants,IReadOnlyList<(string Id,string Version)> CapabilityGrants,
    IReadOnlyList<string> SideEffectGrants,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,long Version,
    string Kind="AUTOMATION",string? ConversationId=null,DevelopmentJobSpec? DevelopmentSpec=null);
public sealed record ProductJobRun(string OwnerPrincipalId,string RunId,string JobId,DateTimeOffset ScheduledFor,string State,long Fence,long Version,
    DateTimeOffset? StartedAt=null,DateTimeOffset? EndedAt=null,string? ModelProfileId=null,string? ContextSnapshotRef=null,string? ErrorCode=null);
public sealed record ProductSettings(string OwnerPrincipalId,string? DefaultChatModelProfileId,string? DefaultLightweightModelProfileId,
    string Timezone,string ApprovalDefaultsJson,string MemoryControlsJson,long Version);
public sealed record JobRunOutput(string OutputRef,string RunId,string Kind,string MediaType,string Summary,string? Text,bool Truncated,DateTimeOffset CreatedAt);
public sealed record JobRunCheckpoint(long Sequence,string Step,string StateJson,long Fence,DateTimeOffset CreatedAt);

public static class JobScheduleCalculator
{
    public static DateTimeOffset? Next(JobSchedule schedule,DateTimeOffset after)
    {
        if(schedule.Kind=="once") return schedule.At>after?schedule.At:null;
        var zone=TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone); var localAfter=TimeZoneInfo.ConvertTime(after,zone);
        var time=schedule.LocalTime??throw new ArgumentException("Recurring schedule requires local time.");
        for(var offset=0;offset<=8;offset++)
        {
            var date=DateOnly.FromDateTime(localAfter.Date).AddDays(offset); if(schedule.Kind=="weekday" && (schedule.Days??[DayOfWeek.Monday,DayOfWeek.Tuesday,DayOfWeek.Wednesday,DayOfWeek.Thursday,DayOfWeek.Friday]).Contains(date.DayOfWeek)==false) continue;
            var unspecified=date.ToDateTime(time,DateTimeKind.Unspecified); if(zone.IsInvalidTime(unspecified)) continue;
            var candidate=new DateTimeOffset(unspecified,zone.GetUtcOffset(unspecified)).ToUniversalTime(); if(candidate>after) return candidate;
        }
        return null;
    }
}