using System;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>A fact that reads Windows path semantics or Win32 APIs: skipped on Linux CI.</summary>
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows path semantics or Win32.";
        }
    }
}
