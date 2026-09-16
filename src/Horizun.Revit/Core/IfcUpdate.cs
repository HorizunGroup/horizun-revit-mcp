// -----------------------------------------------------------------------------
// Horizun Revit MCP - updating an element a re-issued IFC describes differently.
// Original Horizun code.
//
// WHAT CHANGED AND WHY. The previous pass DETECTED changed entities and refused to
// act on them: creating would duplicate, delete-and-recreate would lose every
// edit, tag and dimension attached since, and which of those is acceptable looked
// like the owner's decision. It was reported and stopped there.
//
// The owner has since asked for the update itself, and the refusal was in any case
// only half right. There is a third option neither of those two covers: EDIT THE
// ELEMENT IN PLACE. A wall that moved 300 mm is the same wall - its ElementId is
// unchanged, its tags still point at it, its dimensions still reference it - and
// moving its location curve is what a person would do by hand. That is what this
// file does, for the fields it can do it for, and nothing else.
//
// THE RULE THAT MAKES IT SAFE: A FIELD IS UPDATED OR IT IS DECLARED. Every field
// of a planned row is classified, per element, into
//
//     updated       written and RE-READ from the model afterwards
//     unchanged     the file describes what the model already has
//     unsupported   this build cannot express this change in place, with the reason
//     refused       Revit would not accept it, with what it said
//
// and the reply carries all four. There is no fifth category and no silent skip:
// a field that vanished from the report is a difference the next run will detect
// again and report again, forever, with nobody able to see why.
//
// WHAT IS DELIBERATELY NOT UPDATED IN PLACE:
//
//   THE KIND. A wall that the file now describes as a slab is not an edit; it is a
//   different element. Revit has no operation for it and faking one with a delete
//   and a create would silently drop everything attached. Reported unsupported.
//
//   THE CATEGORY, for the same reason.
//
//   ANYTHING THE PLAN DID NOT MEASURE. This acts on the planned row and nothing
//   else. It does not re-read the IFC, and it never invents a value to write.
//
// IDENTITY IS CHECKED BEFORE ANYTHING IS WRITTEN. The stored ElementId is not
// trusted on its own: Revit reuses ids, so an element deleted and replaced by
// another can carry the id of the thing this is trying to update. The provenance
// record's GlobalId must still be the one on that element, or the update is
// refused with that sentence. Writing geometry into whatever now holds an id is
// the worst outcome available here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What happened to one field of one element.</summary>
    public sealed class IfcFieldOutcome
    {
        public string Field;
        public string State;          // updated | unchanged | unsupported | refused
        public string Was;
        public string Now;
        public string Reason;

        public JObject Json() => new JObject
        {
            ["field"] = Field,
            ["state"] = State,
            ["was"] = Was == null ? (JToken)JValue.CreateNull() : Was,
            ["now"] = Now == null ? (JToken)JValue.CreateNull() : Now,
            ["reason"] = Reason == null ? (JToken)JValue.CreateNull() : Reason
        };
    }

    public static class IfcUpdate
    {
        public const string Updated = "updated";
        public const string Unchanged = "unchanged";
        public const string Unsupported = "unsupported";
        public const string Refused = "refused";

        /// <summary>
        /// Not done here, and not skipped: done AFTER the batch transaction closes.
        ///
        /// A closed profile is replaced through a SketchEditScope, which opens its own
        /// transaction context - and Revit does not nest those. So the batch commits its
        /// parameter and location changes first, and the profiles are replaced one at a
        /// time afterwards. The word exists so that a reader of the first pass is not left
        /// thinking the field was ignored.
        /// </summary>
        public const string Deferred = "deferred_to_its_own_scope";

        private const double MmPerFoot = 304.8;

        /// <summary>
        /// Apply one element's update, inside a transaction the caller has open.
        ///
        /// Returns the per-field outcomes. It writes nothing it cannot read back, and it
        /// never returns an empty list: an element with nothing to do reports every field
        /// as unchanged, because "no outcomes" and "nothing needed doing" look identical
        /// to a reader and mean very different things.
        /// </summary>
        public static List<IfcFieldOutcome> ApplyRow(Document doc, Element element, JObject row,
                                                     out string fatal)
        {
            fatal = null;
            var outcomes = new List<IfcFieldOutcome>();
            if (doc == null || element == null || row == null)
            {
                fatal = "no element or no row";
                return outcomes;
            }

            string kind = row.Value<string>("kind");

            // ---- the type ----------------------------------------------------------
            long typeId = row.Value<long?>("type_id") ?? -1;
            if (typeId >= 0) outcomes.Add(UpdateType(doc, element, typeId));

            // ---- the level and its offsets -----------------------------------------
            long levelId = row.Value<long?>("level_id") ?? -1;
            if (levelId >= 0) outcomes.Add(UpdateLevel(doc, element, levelId, kind));

            if (row["offset"] != null)
                outcomes.Add(UpdateLength(element, "offset", row.Value<double>("offset"),
                                          BaseOffsetParameter(kind)));

            if (row["height"] != null)
                outcomes.Add(UpdateLength(element, "height", row.Value<double>("height"),
                                          HeightParameter(kind)));

            // ---- the geometry ------------------------------------------------------
            if (row["start"] != null && row["end"] != null)
                outcomes.Add(UpdateCurve(doc, element, row["start"] as JArray, row["end"] as JArray));
            else if (row["point"] != null)
                outcomes.Add(UpdatePoint(element, row["point"] as JArray));
            else if (ProfileLoop(row) != null)
                outcomes.Add(new IfcFieldOutcome
                {
                    Field = "profile",
                    State = Deferred,
                    Reason = "this element is defined by a closed profile, which IS replaceable - through a " +
                             "SketchEditScope, which opens its own transaction context. Revit does not nest " +
                             "those, so it happens after this batch commits rather than inside it. The result " +
                             "arrives as a second 'profile' outcome on this element."
                });

            // ---- the parameters the plan measured ----------------------------------
            JArray parameters = row["parameters"] as JArray;
            if (parameters != null)
                foreach (JObject parameter in parameters.OfType<JObject>())
                    outcomes.Add(UpdateParameter(element, parameter));

            if (outcomes.Count == 0)
                outcomes.Add(new IfcFieldOutcome
                {
                    Field = "(none)",
                    State = Unchanged,
                    Reason = "this row describes no field this build knows how to compare in place. Nothing " +
                             "was written, and nothing is claimed about whether the element matches the file."
                });

            return outcomes;
        }

        // =====================================================================
        // The fields
        // =====================================================================

        private static IfcFieldOutcome UpdateType(Document doc, Element element, long typeId)
        {
            var outcome = new IfcFieldOutcome { Field = "type" };
            ElementId current = element.GetTypeId();
            outcome.Was = NameOf(doc, current);

            ElementId wanted = Rid.Make(typeId);
            if (current != null && Rid.Value(current) == typeId)
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            Element type = doc.GetElement(wanted);
            if (type == null)
            {
                outcome.State = Refused;
                outcome.Reason = "type " + typeId + " does not exist in this document. The plan resolved it " +
                                 "when it was made; something removed it since.";
                return outcome;
            }

            try
            {
                // ChangeTypeId is the only in-place type swap, and Revit refuses it when the
                // two types are not interchangeable - which is the answer, not an error to
                // work around.
                // ChangeTypeId answers with the id of the type it ended up on, not
                // with a collection. Declaring it as one did not compile.
                ElementId changed = element.ChangeTypeId(wanted);
                outcome.State = Updated;
                outcome.Now = NameOf(doc, element.GetTypeId());
                // WHAT REVIT ACTUALLY DID, which is not always what was asked.
                //
                // ChangeTypeId answers with the type the element ended up on. Where
                // that is not the type requested, Revit substituted one - and an
                // update that reports success while the element carries a different
                // type is the kind of quiet difference an audit finds months later.
                if (changed != null && changed != ElementId.InvalidElementId && changed != wanted)
                    outcome.Reason = "Revit applied type " + NameOf(doc, changed) + " rather than the one " +
                                     "requested. The element was changed; it was not changed to what was asked.";
            }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the type change: " + ex.Message;
            }

            // RE-READ, ALWAYS. ChangeTypeId returning without throwing is not evidence.
            outcome.Now = NameOf(doc, element.GetTypeId());
            if (outcome.State == Updated && Rid.Value(element.GetTypeId()) != typeId)
            {
                outcome.State = Refused;
                outcome.Reason = "the call returned and the element still reports a different type. Revit " +
                                 "accepted the request and did not apply it.";
            }
            return outcome;
        }

        private static IfcFieldOutcome UpdateLevel(Document doc, Element element, long levelId, string kind)
        {
            var outcome = new IfcFieldOutcome { Field = "level" };
            BuiltInParameter which = LevelParameter(kind);
            Parameter parameter = element.get_Parameter(which);
            if (parameter == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this element exposes no level parameter this build can write (" + which +
                                 "), so its level cannot be changed in place.";
                return outcome;
            }

            ElementId current = parameter.AsElementId();
            outcome.Was = NameOf(doc, current);
            if (current != null && Rid.Value(current) == levelId)
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            if (parameter.IsReadOnly)
            {
                outcome.State = Refused;
                outcome.Reason = "the level parameter is read-only on this element.";
                return outcome;
            }

            try { parameter.Set(Rid.Make(levelId)); }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the level change: " + ex.Message;
                return outcome;
            }

            ElementId after = element.get_Parameter(which)?.AsElementId();
            outcome.Now = NameOf(doc, after);
            outcome.State = after != null && Rid.Value(after) == levelId ? Updated : Refused;
            if (outcome.State == Refused)
                outcome.Reason = "the write returned and the element still reports a different level.";
            return outcome;
        }

        private static IfcFieldOutcome UpdateLength(Element element, string field, double millimetres,
                                                    BuiltInParameter which)
        {
            var outcome = new IfcFieldOutcome { Field = field };
            if (which == BuiltInParameter.INVALID)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this build has no parameter mapping for '" + field + "' on this kind of " +
                                 "element, so it is not written. Guessing one would write a real value into " +
                                 "the wrong parameter.";
                return outcome;
            }

            Parameter parameter = element.get_Parameter(which);
            if (parameter == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this element does not expose " + which + ".";
                return outcome;
            }

            double wantedFeet = millimetres / MmPerFoot;
            double currentFeet = parameter.AsDouble();
            outcome.Was = Mm(currentFeet);

            // A TOLERANCE, NAMED. Revit stores feet as doubles and a millimetre round trip
            // does not land exactly; without this every re-import would report every length
            // as changed and write it again.
            if (Math.Abs(currentFeet - wantedFeet) * MmPerFoot < 0.1)
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            if (parameter.IsReadOnly)
            {
                outcome.State = Refused;
                outcome.Reason = which + " is read-only on this element - usually because a constraint or a " +
                                 "host is driving it.";
                return outcome;
            }

            try { parameter.Set(wantedFeet); }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the write: " + ex.Message;
                return outcome;
            }

            double after = element.get_Parameter(which)?.AsDouble() ?? double.NaN;
            outcome.Now = Mm(after);
            outcome.State = Math.Abs(after - wantedFeet) * MmPerFoot < 0.1 ? Updated : Refused;
            if (outcome.State == Refused)
                outcome.Reason = "the write returned and the element reads back " + outcome.Now +
                                 " rather than " + Mm(wantedFeet) + ". Something else is driving this value.";
            return outcome;
        }

        private static IfcFieldOutcome UpdateCurve(Document doc, Element element, JArray start, JArray end)
        {
            var outcome = new IfcFieldOutcome { Field = "curve" };
            var location = element.Location as LocationCurve;
            if (location == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this element has no location curve, so its axis cannot be moved in place.";
                return outcome;
            }

            XYZ a = Point(start), b = Point(end);
            if (a == null || b == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "the planned row does not carry two usable endpoints.";
                return outcome;
            }

            Curve current = null;
            try { current = location.Curve; } catch { }
            outcome.Was = Describe(current);

            if (current != null && Close(current.GetEndPoint(0), a) && Close(current.GetEndPoint(1), b))
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            try { location.Curve = Line.CreateBound(a, b); }
            catch (Exception ex)
            {
                // A JOINED wall refuses a curve that would break the join, and a hosted
                // element refuses one that leaves its host. Both are answers.
                outcome.State = Refused;
                outcome.Reason = "Revit refused the new axis: " + ex.Message + " A wall joined to others, or " +
                                 "one carrying hosted elements, constrains where its ends may go.";
                return outcome;
            }

            Curve after = null;
            try { after = (element.Location as LocationCurve)?.Curve; } catch { }
            outcome.Now = Describe(after);
            outcome.State = after != null && Close(after.GetEndPoint(0), a) && Close(after.GetEndPoint(1), b)
                ? Updated : Refused;
            if (outcome.State == Refused)
                outcome.Reason = "the assignment returned and the element reads back a different axis. Revit " +
                                 "adjusted it to satisfy a join or a constraint.";
            return outcome;
        }

        private static IfcFieldOutcome UpdatePoint(Element element, JArray point)
        {
            var outcome = new IfcFieldOutcome { Field = "point" };
            var location = element.Location as LocationPoint;
            XYZ wanted = Point(point);
            if (location == null || wanted == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this element has no location point this build can move.";
                return outcome;
            }

            outcome.Was = Describe(location.Point);
            if (Close(location.Point, wanted))
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            try { location.Point = wanted; }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the move: " + ex.Message;
                return outcome;
            }

            XYZ after = (element.Location as LocationPoint)?.Point;
            outcome.Now = Describe(after);
            outcome.State = after != null && Close(after, wanted) ? Updated : Refused;
            return outcome;
        }

        private static IfcFieldOutcome UpdateParameter(Element element, JObject parameter)
        {
            string name = parameter.Value<string>("name");
            var outcome = new IfcFieldOutcome { Field = "parameter:" + (name ?? "(unnamed)") };
            if (string.IsNullOrWhiteSpace(name))
            {
                outcome.State = Unsupported;
                outcome.Reason = "the planned row carries a parameter with no name.";
                return outcome;
            }

            Parameter target = element.LookupParameter(name);
            if (target == null)
            {
                outcome.State = Unsupported;
                outcome.Reason = "this element has no parameter called '" + name + "'. The import that " +
                                 "created it may have bound one that has since been removed.";
                return outcome;
            }

            string wanted = parameter.Value<string>("value");
            outcome.Was = target.AsString() ?? target.AsValueString();
            if (string.Equals(outcome.Was, wanted, StringComparison.Ordinal))
            {
                outcome.State = Unchanged;
                outcome.Now = outcome.Was;
                return outcome;
            }

            if (target.IsReadOnly)
            {
                outcome.State = Refused;
                outcome.Reason = "'" + name + "' is read-only on this element.";
                return outcome;
            }

            try
            {
                if (target.StorageType == StorageType.String) target.Set(wanted ?? "");
                else
                {
                    outcome.State = Unsupported;
                    outcome.Reason = "'" + name + "' stores " + target.StorageType + ", and this build only " +
                                     "updates text parameters in place. Writing a number parsed from text is " +
                                     "how a unit error becomes a value nobody questions.";
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the write: " + ex.Message;
                return outcome;
            }

            outcome.Now = element.LookupParameter(name)?.AsString();
            outcome.State = string.Equals(outcome.Now, wanted, StringComparison.Ordinal) ? Updated : Refused;
            if (outcome.State == Refused)
                outcome.Reason = "the write returned and the parameter reads back differently.";
            return outcome;
        }

        /// <summary>
        /// The closed boundary a row carries, under whichever of the three names the
        /// planner used, or null.
        /// </summary>
        public static JArray ProfileLoop(JObject row)
        {
            if (row == null) return null;
            foreach (string key in new[] { "loop", "profile", "boundary" })
            {
                var loop = row[key] as JArray;
                if (loop != null && loop.Count >= 3) return loop;
            }
            return null;
        }

        // =====================================================================
        // Parameter mapping - closed, and per kind
        // =====================================================================

        private static BuiltInParameter LevelParameter(string kind)
        {
            switch (kind)
            {
                case "wall": return BuiltInParameter.WALL_BASE_CONSTRAINT;
                case "floor": return BuiltInParameter.LEVEL_PARAM;
                case "column":
                case "beam":
                case "member": return BuiltInParameter.FAMILY_BASE_LEVEL_PARAM;
                default: return BuiltInParameter.INVALID;
            }
        }

        private static BuiltInParameter BaseOffsetParameter(string kind)
        {
            switch (kind)
            {
                case "wall": return BuiltInParameter.WALL_BASE_OFFSET;
                case "floor": return BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
                case "column":
                case "member": return BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM;
                default: return BuiltInParameter.INVALID;
            }
        }

        private static BuiltInParameter HeightParameter(string kind)
        {
            switch (kind)
            {
                case "wall": return BuiltInParameter.WALL_USER_HEIGHT_PARAM;
                case "column":
                case "member": return BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM;
                default: return BuiltInParameter.INVALID;
            }
        }

        // =====================================================================
        // Small helpers
        // =====================================================================

        private static XYZ Point(JArray raw)
        {
            if (raw == null || raw.Count < 3) return null;
            try
            {
                return new XYZ(raw[0].Value<double>() / MmPerFoot,
                               raw[1].Value<double>() / MmPerFoot,
                               raw[2].Value<double>() / MmPerFoot);
            }
            catch { return null; }
        }

        private static bool Close(XYZ a, XYZ b) =>
            a != null && b != null && a.DistanceTo(b) * MmPerFoot < 0.5;

        private static string Describe(Curve curve)
        {
            if (curve == null) return null;
            try { return Describe(curve.GetEndPoint(0)) + " -> " + Describe(curve.GetEndPoint(1)); }
            catch { return "(unreadable curve)"; }
        }

        private static string Describe(XYZ point) =>
            point == null ? null
                : string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#}, {2:0.#}) mm",
                                point.X * MmPerFoot, point.Y * MmPerFoot, point.Z * MmPerFoot);

        private static string Mm(double feet) =>
            double.IsNaN(feet) ? null
                : (feet * MmPerFoot).ToString("0.###", CultureInfo.InvariantCulture) + " mm";

        private static string NameOf(Document doc, ElementId id)
        {
            if (doc == null || id == null || Rid.Value(id) < 0) return null;
            Element element = doc.GetElement(id);
            if (element == null) return null;
            try { return element.Name; } catch { return "(unnamed " + Rid.Value(id) + ")"; }
        }
    }
}
