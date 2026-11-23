using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    internal sealed class LeaderHit
    {
        public bool DepthMeasured = true;
        public bool Cast { get; set; }
        public string Coverage { get; set; } // complete | partial | no_3d_view | not_cast
        public bool AmbiguousRay { get; set; }
        public string Source { get; set; } // host | link | null
        public long LinkInstanceId { get; set; } = -1;
        public string LinkInstanceUniqueId { get; set; }
        public long ElementId { get; set; } = -1;
        public string UniqueId { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string LayerMaterial { get; set; }
        public string LayerResolution { get; set; }
        public string LastChangedBy { get; set; }
        public string Uncertainty { get; set; }
        /// <summary>Why this hit was taken without a face (a start point inside the element, cut by the plan's cut plane); null for a ray hit.</summary>
        public string Note { get; set; }
        public int LayerIndex { get; set; } = -1;
        public double DistanceFeet { get; set; }
        /// <summary>How many link levels the hit is inside: 0 host, 1 a link, 2+ a nested link.</summary>
        public int LinkDepth { get; set; }
        public IReadOnlyList<string> LinkPath { get; set; } = Array.Empty<string>();
        public List<string> AlsoHit { get; } = new List<string>();
        public Element Element { get; set; } // the resolved element in its own document
        public Document ElementDocument { get; set; }
        public LeaderHit FirstRayHit { get; set; } // observed face when the target at the origin is uncertain

        public string TargetIdentity
        {
            get
            {
                if (!Cast || Coverage != "complete" || AmbiguousRay || string.IsNullOrEmpty(UniqueId)) return null;
                if (Source == "host") return "host|" + UniqueId;
                if (Source != "link" || LinkDepth == 0 || LinkPath.Count != LinkDepth ||
                    LinkPath.Any(string.IsNullOrEmpty)) return null;
                return "link|" + string.Join("|", LinkPath) + "|" + UniqueId;
            }
        }

        public JObject ToJson()
        {
            const double Mm = 304.8;
            var o = new JObject
            {
                ["coverage"] = Coverage,
                ["source"] = Source
            };
            if (Source == null)
            {
                o["hit"] = false;
                if (Uncertainty != null) o["uncertainty"] = Uncertainty;
                if (FirstRayHit != null) o["first_ray_hit"] = FirstRayHit.ToJson();
                return o;
            }
            o["hit"] = true;
            o["element_id"] = ElementId;
            o["unique_id"] = UniqueId;
            if (Source == "link")
            {
                o["link_instance_id"] = LinkInstanceId;
                o["link_instance_unique_id"] = LinkInstanceUniqueId;
                if (LinkPath.Count > 0) o["link_path_unique_ids"] = new JArray(LinkPath);
            }
            o["category"] = Category;
            o["type_name"] = TypeName;
            o["layer_index"] = LayerIndex >= 0 ? (JToken)LayerIndex : JValue.CreateNull();
            o["layer_material"] = LayerMaterial;
            o["layer_resolution"] = LayerResolution;
            o["last_changed_by"] = LastChangedBy;
            o["distance_mm"] = DepthMeasured ? (JToken)Math.Round(DistanceFeet * Mm, 2) : JValue.CreateNull();
            if (AlsoHit.Count > 0) o["also_hit"] = new JArray(AlsoHit);
            if (Uncertainty != null) o["uncertainty"] = Uncertainty;
            if (Note != null) o["note"] = Note;
            return o;
        }

        internal static LeaderHit Partial(string reason, bool cast, LeaderHit ray) => new LeaderHit
        {
            Cast = cast, Coverage = "partial", Uncertainty = reason,
            FirstRayHit = ray?.Source == null ? null : ray
        };
    }
}
