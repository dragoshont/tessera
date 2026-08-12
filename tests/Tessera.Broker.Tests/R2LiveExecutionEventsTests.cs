using Xunit;

namespace Tessera.Broker.Tests;

public sealed class R2LiveExecutionEventsTests
{
    [Fact]
    public void Terminal_stream_state_expires_after_bounded_retention()
    {
        var clock=new AdjustableTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));var events=new R2LiveExecutionEvents(clock);
        events.PublishText("owner","conversation","execution","{\"delta\":\"hello\"}",5);events.MarkTerminal("owner","conversation","execution");
        Assert.Single(events.ListAfter("owner","conversation","execution",0));Assert.Empty(events.ListAfter("other","conversation","execution",0));Assert.Equal(1,events.StreamCount);

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(events.ListAfter("owner","conversation","execution",0));Assert.Equal(0,events.StreamCount);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now):TimeProvider
    {
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan duration)=>now+=duration;
    }
}