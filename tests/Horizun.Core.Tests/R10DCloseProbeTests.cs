using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

public class R10DCloseProbeTests
{
    [Fact]
    public void RecordsEntryAndExitEvenWhenHandlerThrows()
    {
        var lines = new List<string>();
        Assert.Throws<InvalidOperationException>((Action)(() => { using (CloseProbe.Enter("DocumentClosing", (name, phase) => lines.Add(name + " " + phase))) { throw new InvalidOperationException(); } }));
        Assert.Equal(new[] { "DocumentClosing enter", "DocumentClosing exit" }, lines);
    }

    [Fact]
    public void BrokenLoggerDoesNotStopHandler()
    {
        bool ran = false;
        using (CloseProbe.Enter("handler", (_, __) => throw new Exception())) { ran = true; }
        Assert.True(ran);
    }
}

