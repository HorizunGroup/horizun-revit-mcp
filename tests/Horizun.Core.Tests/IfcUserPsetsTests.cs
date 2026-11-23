// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// MZ-13: the user-defined property-set file named by an IFC export configuration.
// A configuration that turns it on and names a file that is not there used to
// export without the sets and say nothing. These tests pin the decision: present,
// absent (a structured warning), inactive (no change), a path stored with doubled
// separators, a path that cannot be resolved, a configuration that cannot be read,
// and the check of the exported file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcUserPsetsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-mz13-" + Guid.NewGuid().ToString("N"));

        public IfcUserPsetsTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private const string PsetFile =
            "PropertySet:\tPset_HZ\tI\tIfcWall\n" +
            "\tCode\tText\tHZ_Code\n" +
            "PropertySet:\tPset_HZ_Type\tT\tIfcWall\n" +
            "\tMaker\tLabel\n";

        private string WriteFile(string name = "HZ_Pset_IFC.txt", string text = PsetFile)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private static JObject Config(bool active, string path) => new JObject
        {
            ["ExportUserDefinedPsets"] = active,
            ["ExportUserDefinedPsetsFileName"] = path
        };



























        private static IfcStepReader.Document Ifc(string data)
        {
            string error;
            IfcStepReader.Document d = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('x'),'2;1');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" + data +
                "\nENDSEC;\nEND-ISO-10303-21;\n", out error);
            Assert.Null(error);
            return d;
        }





        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, JObject>> Saved(params object[] pairs)
        {
            var l = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, JObject>>();
            for (int i = 0; i < pairs.Length; i += 2)
                l.Add(new System.Collections.Generic.KeyValuePair<string, JObject>((string)pairs[i], (JObject)pairs[i + 1]));
            return l;
        }









































        private const string TwoSetsIfc =
            "#2=IFCWALL('0aaaaaaaaaaaaaaaaaaaW1',$,'A',$,$,$,$,$,.SOLIDWALL.);\n" +
            "#4=IFCWALLTYPE('0aaaaaaaaaaaaaaaaaaaT1',$,'WT',$,$,(#12),$,$,$,.SOLIDWALL.);\n" +
            "#10=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaS1',$,'Pset_HZ',$,(#11));\n" +
            "#11=IFCPROPERTYSINGLEVALUE('Other',$,IFCLABEL('A-01'),$);\n" +
            "#12=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaS2',$,'Pset_HZ_Type',$,(#13));\n" +
            "#13=IFCPROPERTYSINGLEVALUE('Maker',$,IFCLABEL('X'),$);\n" +
            "#14=IFCRELDEFINESBYPROPERTIES('0aaaaaaaaaaaaaaaaaaaR1',$,$,$,(#2),#10);\n" +
            "#15=IFCRELDEFINESBYTYPE('0aaaaaaaaaaaaaaaaaaaR2',$,$,$,(#2),#4);";







        [Fact]
        public void The_confirmation_token_binds_ifc_setup_and_strict_and_every_earlier_field()
        {
            string[] keys = IfcDeliveryRules.PlanHashKeys;
            foreach (string k in new[] { "ifc_setup", "strict", "pset_mapping_path", "ifc_version", "output_folder", "overwrite", "ids_path" })
                Assert.Contains(k, keys);
            Assert.Equal(keys.Length, keys.Distinct().Count());
        }
    }
}
