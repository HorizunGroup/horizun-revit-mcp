// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_manage_views create_room_isometric. Original Horizun code.
//
// 4.2-N4 (CT-P2-2d): a 3D view of ONE room from one of its corners, the section box fitted to the
// room plus a margin. The camera and the box come from RoomIsometricRules; this file reads the room,
// creates the isometric, and re-reads the box and the orientation after the commit.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsRoomIsometricOperation(string op) => string.Equals(op, "create_room_isometric", StringComparison.OrdinalIgnoreCase);

        private const double RoomIsoTolerance = 0.5 / 304.8;

        private static double[][] RoomBox(Room room, double margin)
        {
            BoundingBoxXYZ b = room.get_BoundingBox(null);
            if (b == null) throw new ArgumentException("room " + Rid.Value(room.Id) + " has no readable bounding box (not placed, or not enclosed).");
            return RoomIsometricRules.Box(new[] { b.Min.X, b.Min.Y, b.Min.Z }, new[] { b.Max.X, b.Max.Y, b.Max.Z }, margin);
        }

        /// <summary>Refuses, before anything is written, what cannot be built; records the plan on the action for the re-read.</summary>
        internal static void ValidateRoomIsometric(Document doc, JObject a)
        {
            string error = RoomIsometricRules.Validate(a);
            if (error != null) throw new ArgumentException(error);
            Room room = Need<Room>(doc, a, "room_id");
            if (room.Area <= 0) throw new ArgumentException("room " + Rid.Value(room.Id) + " has no area: it is not placed or not enclosed. Nothing was created.");
            double unit = UnitsOf(a);
            // the default margin is stated in millimetres; a caller's margin is in the call's units
            double marginFeet = a["margin"] == null ? RoomIsometricRules.DefaultMarginMm / 304.8 : a.Value<double>("margin") * unit;
            RoomBox(room, marginFeet); // refuses a room without extent
            if (a["name"] == null || string.IsNullOrWhiteSpace(a.Value<string>("name")))
                a["name"] = RoomIsometricRules.DefaultName(room.Number, room.Name);
            Validate3DView(a, false);
        }

        internal static View3D ApplyRoomIsometric(Document doc, JObject a, ViewFamilyType type)
        {
            Room room = Need<Room>(doc, a, "room_id");
            double unit = UnitsOf(a);
            double marginFeet = a["margin"] == null ? RoomIsometricRules.DefaultMarginMm / 304.8 : a.Value<double>("margin") * unit;
            double[][] box = RoomBox(room, marginFeet);
            string direction = (a.Value<string>("direction") ?? RoomIsometricRules.DefaultDirection).Trim().ToLowerInvariant();
            double[] fwd = RoomIsometricRules.Forward(direction), up = RoomIsometricRules.Up(direction);
            var centre = new XYZ((box[0][0] + box[1][0]) / 2, (box[0][1] + box[1][1]) / 2, (box[0][2] + box[1][2]) / 2);
            double reach = Math.Max(box[1][0] - box[0][0], Math.Max(box[1][1] - box[0][1], box[1][2] - box[0][2])) * 2 + 10;
            var forward = new XYZ(fwd[0], fwd[1], fwd[2]);
            View3D view = View3D.CreateIsometric(doc, type.Id);
            view.Name = a.Value<string>("name");
            view.SetOrientation(new ViewOrientation3D(centre - forward * reach, new XYZ(up[0], up[1], up[2]), forward));
            var section = new BoundingBoxXYZ { Transform = Transform.Identity, Min = new XYZ(box[0][0], box[0][1], box[0][2]), Max = new XYZ(box[1][0], box[1][1], box[1][2]) };
            view.SetSectionBox(section);
            view.IsSectionBoxActive = true;
            var sent = new JObject();
            Apply3DStyle(view, a, sent);
            sent["room_id"] = Rid.Value(room.Id);
            sent["direction"] = direction;
            sent["section_box_feet"] = new JArray(new JArray(box[0]), new JArray(box[1]));
            sent["forward"] = new JArray(fwd);
            a["__view3d"] = sent;
            a["__room_iso"] = true;
            return view;
        }

        internal static bool VerifyRoomIsometric(Document doc, JObject a, Element e)
        {
            if (!(e is View3D view) || !(a["__view3d"] is JObject sent)) return false;
            bool ok = view.IsSectionBoxActive && !view.IsPerspective;
            double[][] want = { sent["section_box_feet"][0].ToObject<double[]>(), sent["section_box_feet"][1].ToObject<double[]>() };
            BoundingBoxXYZ got = view.GetSectionBox();
            bool boxOk = got != null && RoomIsometricRules.Contains(
                new[] { new[] { got.Min.X, got.Min.Y, got.Min.Z }, new[] { got.Max.X, got.Max.Y, got.Max.Z } }, want, RoomIsoTolerance) &&
                RoomIsometricRules.Contains(want, new[] { new[] { got.Min.X, got.Min.Y, got.Min.Z }, new[] { got.Max.X, got.Max.Y, got.Max.Z } }, RoomIsoTolerance);
            double[] f = sent["forward"].ToObject<double[]>();
            XYZ gf = view.GetOrientation().ForwardDirection;
            bool dirOk = Math.Abs(gf.X - f[0]) < 1e-6 && Math.Abs(gf.Y - f[1]) < 1e-6 && Math.Abs(gf.Z - f[2]) < 1e-6;
            bool styleOk = sent["display_style"] == null || string.Equals(view.DisplayStyle.ToString(), (string)sent["display_style"], StringComparison.Ordinal);
            sent["reread"] = new JObject { ["section_box_active"] = view.IsSectionBoxActive, ["section_box_ok"] = boxOk, ["direction_ok"] = dirOk, ["style_ok"] = styleOk, ["view_id"] = Rid.Value(view.Id), ["name"] = view.Name };
            ok = ok && boxOk && dirOk && styleOk;
            sent["verified"] = ok;
            return ok;
        }
    }
}
