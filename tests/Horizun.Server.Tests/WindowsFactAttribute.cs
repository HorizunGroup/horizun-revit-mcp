using System;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>A fact that runs Windows PowerShell or reads Windows paths: skipped on Linux CI.</summary>
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows PowerShell.";
        }
    }
}
