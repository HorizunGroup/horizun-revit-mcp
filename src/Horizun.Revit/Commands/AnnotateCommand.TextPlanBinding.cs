using System;
using System.Collections.Generic;
using System.Globalization;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        private static void CapturePlannedLeaderIdentity(TextLayout text)
        {
            if (text.TargetElementId.HasValue || text.TargetLinkedId.HasValue)
                text.PlannedLeaderIdentity = text.Hit?.TargetIdentity
                    ?? throw new ArgumentException("the aiming ray did not establish a stable target identity. Nothing was written.");
        }

        /// <summary>The paper facts bound into the approved plan when requested.</summary>
        private static void BindTextPlan(Plan plan, IDictionary<string, string> before)
        {
            TextLayout text = plan.TextPlan;
            if (plan.Operation != "text" || text == null || !text.Any) return;
            if (text.WidthFeet.HasValue) before["text.width_paper_feet"] = DimensionPlanRules.CanonicalFeet(text.WidthFeet.Value);
            if (text.ExpectedLines.HasValue) before["text.expected_lines"] = text.ExpectedLines.Value.ToString(CultureInfo.InvariantCulture);
            if (!text.HasLeader) return;
            before["leader.form"] = text.TargetForm + "|" + text.Side + "|" + (text.Attachment ?? "");
            before["leader.end"] = text.End == null ? "" : DimensionPlanRules.CanonicalPoint(text.End.X, text.End.Y, text.End.Z);
            before["leader.hit"] = text.PlannedLeaderIdentity ?? text.Hit?.TargetIdentity ?? text.Hit?.Coverage ?? "not_cast";
        }
    }
}
