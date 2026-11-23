// -----------------------------------------------------------------------------
// Horizun Revit MCP - the Revit half of Core/LevelResolutionRules.
//
// Reads ONE source of an element's level and says what it found. The order and
// the stopping rule are the rules class's; nothing here decides which source wins.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class ElementLevelReader
    {
        // Parsed once. A token this Revit version does not define is simply absent:
        // the order is shared across 2023-2027 and an unknown name is not an error.
        private static readonly Dictionary<string, BuiltInParameter> Parameters = ParseParameters();

        private readonly Document doc;
        // Level id -> name, or null for an id that is not a Level. Per document: ids
        // repeat across links.
        private readonly Dictionary<long, string> names = new Dictionary<long, string>();
        private readonly HashSet<long> notLevels = new HashSet<long>();

        public ElementLevelReader(Document doc) { this.doc = doc; }

        public LevelResolution Resolve(Element element)
        {
            return LevelResolutionRules.Resolve(source => Probe(element, source));
        }

        private LevelProbe Probe(Element element, string source)
        {
            try
            {
                if (source == LevelResolutionRules.ElementLevelId)
                    return FromId(element.LevelId);
                if (source == LevelResolutionRules.HostLevel)
                {
                    var fi = element as FamilyInstance;
                    if (fi == null) return LevelProbe.Absent();
                    Element host = fi.Host;
                    return host is Level ? FromId(host.Id) : LevelProbe.Absent();
                }
                if (source == LevelResolutionRules.RailingHostElement) return FromRailingHost(element);
                BuiltInParameter bip;
                if (!Parameters.TryGetValue(source, out bip)) return LevelProbe.Absent();
                Parameter p = element.get_Parameter(bip);
                if (p == null) return LevelProbe.Absent();
                if (p.StorageType != StorageType.ElementId) return LevelProbe.NoLevel();
                return FromId(p.AsElementId());
            }
            catch (Exception ex) { return LevelProbe.Failed(ex.Message); }
        }

        // Where a stair, ramp or floor keeps its own level, in the order they are asked.
        private static readonly BuiltInParameter[] HostLevelParameters =
        {
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM
        };

        /// <summary>The level of the element a railing is hosted on (a stair's base level, a floor's level).</summary>
        private LevelProbe FromRailingHost(Element element)
        {
            if (!(element is Autodesk.Revit.DB.Architecture.Railing railing)) return LevelProbe.Absent();
            if (!railing.HasHost) return LevelProbe.Absent();
            Element host = doc.GetElement(railing.HostId);
            if (host == null) return LevelProbe.Absent();
            LevelProbe own = FromId(host.LevelId);
            if (own.State == LevelProbeState.Level) return own;
            foreach (BuiltInParameter bip in HostLevelParameters)
            {
                Parameter p = host.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.ElementId) continue;
                LevelProbe probe = FromId(p.AsElementId());
                if (probe.State == LevelProbeState.Level) return probe;
            }
            return LevelProbe.NoLevel();
        }

        private LevelProbe FromId(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return LevelProbe.NoLevel();
            long key = Rid.Value(id);
            if (notLevels.Contains(key)) return LevelProbe.NoLevel();
            if (names.TryGetValue(key, out string cached)) return LevelProbe.Found(cached);
            // A source counts only when it names a LEVEL. An id that resolves to anything
            // else (a host face, a reference plane) is not a floor.
            Level level = doc.GetElement(id) as Level;
            if (level == null) { notLevels.Add(key); return LevelProbe.NoLevel(); }
            string name;
            try { name = level.Name; } catch { name = null; }
            names[key] = name;
            return LevelProbe.Found(name);
        }

        private static Dictionary<string, BuiltInParameter> ParseParameters()
        {
            var map = new Dictionary<string, BuiltInParameter>(StringComparer.Ordinal);
            foreach (string source in LevelResolutionRules.Sources)
            {
                BuiltInParameter bip;
                if (Enum.TryParse(source, false, out bip)) map[source] = bip;
            }
            return map;
        }
    }
}
