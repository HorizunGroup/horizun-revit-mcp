using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{

}

namespace Horizun.Revit.Commands
{
    public sealed partial class MepRoutingCommand
    {
        public string Name => "horizun_mep_routing";
        private sealed class Units { }
        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch { return default; }
        }

        

        
    }
}
