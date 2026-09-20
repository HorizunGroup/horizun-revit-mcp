// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE VIEWPOINT. A BCF topic without one is a topic nobody can navigate to.
//
// The export wrote markup.bcf and nothing else. Every topic in it opened in a
// coordinator's tool as a title and a description with no camera, no selection
// and no image — which is most of what BCF exists for. A count of topics said the
// export worked; the thing a coordinator does with a BCF did not.
//
// WHAT THIS BUILDS, to BCF 2.1:
//
//   visualization.bcfv — a PerspectiveCamera looking at the clash point from a
//   distance derived from the overlap, plus a Components/Selection listing the
//   two sides. That is the whole navigable payload.
//
// THE CAMERA IS AIMED, NOT DEFAULTED. Direction, up vector and position are
// computed so the point is in front of the camera at a readable distance. A
// viewpoint at the project origin looking down +Z is syntactically valid and
// shows a coordinator the underside of a building.
//
// AND THE PART THAT CANNOT BE FAKED: BCF identifies a component by its IFC
// GlobalId. A Revit element has one only after an export that wrote one — and an
// element inside a LINK has no host-document GUID at all. So:
//
//   a side whose IFC GUID is known travels as a Component with that GUID;
//   a side whose GUID is unknown travels as a Component with an
//   AuthoringToolId and NO IfcGuid, and the topic's description says which;
//   a side in a link says so, because a coordinator selecting it in the host
//   model would select nothing and conclude the clash was stale.
//
// Writing a made-up GUID would produce a file that opens, selects nothing, and
// looks like the model changed.
//
// BCF 2.1 RATHER THAN 3.0, and the export says so: 3.0 restructures the markup
// and moves the viewpoint, and shipping a 2.1 file labelled 3.0 is worse than
// shipping 2.1.
//
// Revit-free: it takes numbers and strings and returns XML.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>One side of a finding, as much as BCF can say about it.</summary>
    public sealed class BcfComponent
    {
        /// <summary>The IFC GlobalId, when the model carries one. BCF's only real identity.</summary>
        public string IfcGuid;

        /// <summary>The Revit ElementId as text. Recorded as AuthoringToolId; NOT an identity to others.</summary>
        public string AuthoringToolId;

        /// <summary>The link instance, when this element lives in one.</summary>
        public string LinkInstanceId;

        public string OriginatingSystem = "Horizun Revit MCP";

        public bool InLink => !string.IsNullOrWhiteSpace(LinkInstanceId);
    }

    public static class BcfViewpoint
    {
        public const string Version = "2.1";

        /// <summary>Metres per millimetre. BCF cameras are in METRES; a point in mm is 1000x wrong.</summary>
        public const double MetresPerMillimetre = 0.001;

        /// <summary>How far back the camera sits from the point, in millimetres, when nothing better is known.</summary>
        public const double DefaultStandoffMm = 4000.0;

        public const double DefaultFieldOfView = 60.0;

        /// <summary>
        /// A viewpoint aimed at a point, with both sides selected.
        ///
        /// `pointMm` is in MILLIMETRES, like everything else that crosses this bridge, and is
        /// converted here exactly once. BCF cameras are in metres, and a camera a thousand
        /// times too far out is a viewpoint that opens on an empty grey field — valid, and
        /// indistinguishable from a broken model.
        /// </summary>
        public static string Xml(double[] pointMm, IEnumerable<BcfComponent> components,
                                 double standoffMm = DefaultStandoffMm)
        {
            double[] point = pointMm ?? new double[] { 0, 0, 0 };
            if (standoffMm <= 0) standoffMm = DefaultStandoffMm;

            // A THREE-QUARTER VIEW, not an elevation. Looking straight down an axis at a
            // clash between two pipes shows one pipe. The direction is normalised so the
            // camera sits `standoff` away along it, whatever the standoff is.
            double[] direction = Normalise(new[] { -0.577, -0.577, -0.577 });
            double[] position =
            {
                point[0] - direction[0] * standoffMm,
                point[1] - direction[1] * standoffMm,
                point[2] - direction[2] * standoffMm
            };
            // Up is +Z made perpendicular to the view direction. Left raw it is not
            // perpendicular for any non-horizontal view, and several readers then roll the
            // camera to compensate.
            double dot = direction[2];
            double[] up = Normalise(new[] { -direction[0] * dot, -direction[1] * dot, 1 - direction[2] * dot })
                          ?? new double[] { 0, 0, 1 };

            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            xml.Append("<VisualizationInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" Guid=\"")
               .Append(Guid.NewGuid().ToString("D")).Append("\">\n");

            var list = (components ?? Enumerable.Empty<BcfComponent>()).Where(c => c != null).ToList();
            if (list.Count > 0)
            {
                xml.Append("  <Components>\n    <Selection>\n");
                foreach (BcfComponent component in list)
                {
                    xml.Append("      <Component");
                    // AN IfcGuid ATTRIBUTE IS EITHER TRUE OR ABSENT. A made-up one produces a
                    // file that opens, selects nothing, and reads as though the model changed.
                    if (!string.IsNullOrWhiteSpace(component.IfcGuid))
                        xml.Append(" IfcGuid=\"").Append(Escape(component.IfcGuid)).Append("\"");
                    xml.Append(">\n");
                    xml.Append("        <OriginatingSystem>")
                       .Append(Escape(component.OriginatingSystem)).Append("</OriginatingSystem>\n");
                    if (!string.IsNullOrWhiteSpace(component.AuthoringToolId))
                        xml.Append("        <AuthoringToolId>")
                           .Append(Escape(component.AuthoringToolId +
                                          (component.InLink ? " in link " + component.LinkInstanceId : "")))
                           .Append("</AuthoringToolId>\n");
                    xml.Append("      </Component>\n");
                }
                xml.Append("    </Selection>\n");
                // VISIBILITY DEFAULT TRUE: a BCF that hid everything except the selection
                // would show the clash and none of what it is clashing through.
                xml.Append("    <Visibility DefaultVisibility=\"true\" />\n");
                xml.Append("  </Components>\n");
            }

            xml.Append("  <PerspectiveCamera>\n");
            xml.Append(Vector("CameraViewPoint", Scale(position)));
            xml.Append(Vector("CameraDirection", direction));
            xml.Append(Vector("CameraUpVector", up));
            xml.Append("    <FieldOfView>")
               .Append(DefaultFieldOfView.ToString("R", CultureInfo.InvariantCulture))
               .Append("</FieldOfView>\n");
            xml.Append("  </PerspectiveCamera>\n");
            xml.Append("</VisualizationInfo>\n");
            return xml.ToString();
        }

        /// <summary>The Viewpoints element the markup needs so a reader knows the .bcfv is there.</summary>
        public static string MarkupViewpointXml(string viewpointFile) =>
            "  <Viewpoints Guid=\"" + Guid.NewGuid().ToString("D") + "\">\n" +
            "    <Viewpoint>" + Escape(viewpointFile) + "</Viewpoint>\n" +
            "  </Viewpoints>\n";

        /// <summary>
        /// What this viewpoint can and cannot promise, for the topic's description.
        ///
        /// It goes in the FILE, not only in the reply: the person who opens the BCF in a
        /// coordination tool is not the person who read the export's JSON.
        /// </summary>
        public static string IdentityNote(IEnumerable<BcfComponent> components)
        {
            var list = (components ?? Enumerable.Empty<BcfComponent>()).Where(c => c != null).ToList();
            int withGuid = list.Count(c => !string.IsNullOrWhiteSpace(c.IfcGuid));
            int inLinks = list.Count(c => c.InLink);
            if (list.Count == 0) return null;

            var parts = new List<string>();
            if (withGuid < list.Count)
                parts.Add((list.Count - withGuid) + " of " + list.Count + " component(s) carry NO IfcGuid: " +
                          "this model has not been exported with IFC GUIDs, so selecting them in another " +
                          "tool will select nothing. The Revit element id is recorded instead, which " +
                          "identifies them only inside this model.");
            if (inLinks > 0)
                parts.Add(inLinks + " component(s) live inside a LINKED model. A coordinator selecting " +
                          "them in the host model selects nothing, and would reasonably conclude the " +
                          "issue is stale; the link is named beside each id.");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private static double[] Scale(double[] millimetres) => new[]
        {
            millimetres[0] * MetresPerMillimetre,
            millimetres[1] * MetresPerMillimetre,
            millimetres[2] * MetresPerMillimetre
        };

        private static string Vector(string name, double[] v) =>
            "    <" + name + ">\n" +
            "      <X>" + v[0].ToString("R", CultureInfo.InvariantCulture) + "</X>\n" +
            "      <Y>" + v[1].ToString("R", CultureInfo.InvariantCulture) + "</Y>\n" +
            "      <Z>" + v[2].ToString("R", CultureInfo.InvariantCulture) + "</Z>\n" +
            "    </" + name + ">\n";

        private static double[] Normalise(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (length < 1e-9 || double.IsNaN(length)) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static string Escape(string text)
        {
            if (text == null) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;");
        }
    }
}
