// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_wall_sweep operation=place_edge: slab edges,
// fascias and gutters.
//
// Document.NewSlabEdge / NewFascia / NewGutter take a TYPE and the REFERENCES of
// the edges of a host element (a floor for a slab edge; a roof for a fascia or a
// gutter). Nothing in the model says which edge the caller means, and a model
// editor's index is not stable, so the edges are named by what Revit itself calls
// a stable reference (Reference.ConvertToStableRepresentation), read with
// operation=read host_id=N and passed back unchanged. A face may be named instead:
// the apply takes every edge of it.
//
// THE ONE THING THIS DOES NOT DECIDE: whether the edges chain into one run. Revit
// refuses edges that do not, and its message comes back as the refusal.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class HostedEdgeRules
    {
        public const string SlabEdge = "slab_edge";
        public const string Fascia = "fascia";
        public const string Gutter = "gutter";

        public static readonly string[] Kinds = { SlabEdge, Fascia, Gutter };

        public const int MaxEdgeReferences = 200;
        public const int MaxFaceReferences = 50;

        /// <summary>The fields place_edge takes, beside the common ones the validator already allows.</summary>
        public static readonly string[] PlaceEdgeFields = { "kind", "type_id", "host_id", "edge_references", "face_references" };

        /// <summary>The element class Revit makes for a kind.</summary>
        public static string CreatedClass(string kind)
        {
            switch (kind)
            {
                case SlabEdge: return "SlabEdge";
                case Fascia: return "Fascia";
                case Gutter: return "Gutter";
                default: return null;
            }
        }

        /// <summary>The element-type class a kind needs.</summary>
        public static string TypeClass(string kind)
        {
            switch (kind)
            {
                case SlabEdge: return "SlabEdgeType";
                case Fascia: return "FasciaType";
                case Gutter: return "GutterType";
                default: return null;
            }
        }

        /// <summary>
        /// Whether a host of this class chain can carry the kind. The chain is the host's own class name and
        /// its base classes' names, so a FootPrintRoof counts as a RoofBase. Revit has the last word: a host
        /// that passes here and is still refused comes back as Revit's message.
        /// </summary>
        public static bool HostFits(string kind, IEnumerable<string> classChain)
        {
            if (classChain == null) return false;
            var chain = new HashSet<string>(classChain, StringComparer.Ordinal);
            switch (kind)
            {
                case SlabEdge: return chain.Contains("Floor") || chain.Contains("SlabEdge");
                case Fascia:
                case Gutter: return chain.Contains("RoofBase") || chain.Contains("Fascia") || chain.Contains("Gutter");
                default: return false;
            }
        }

        public static string HostRequirement(string kind)
        {
            switch (kind)
            {
                case SlabEdge: return "a floor or slab (or another slab edge)";
                case Fascia: return "a roof (or a fascia or gutter already on one)";
                case Gutter: return "a roof (or a fascia or gutter already on one)";
                default: return "a host element";
            }
        }

        /// <summary>Null when the request is a well-formed place_edge; otherwise why not.</summary>
        public static string Validate(JObject request)
        {
            if (request == null) return "parameters must be a JSON object.";
            string kind = request.Value<string>("kind");
            if (kind == null || !Kinds.Contains(kind))
                return "kind is required: " + string.Join(", ", Kinds) + " - which sweep is made is not guessed.";
            if (request["type_id"]?.Type != JTokenType.Integer)
                return "type_id (a " + TypeClass(kind) + ", from operation=read with host_id) is required.";
            if (request["host_id"]?.Type != JTokenType.Integer)
                return "host_id (" + HostRequirement(kind) + ") is required.";

            string edges = CheckList(request, "edge_references", MaxEdgeReferences);
            if (edges != null) return edges;
            string faces = CheckList(request, "face_references", MaxFaceReferences);
            if (faces != null) return faces;
            int edgeCount = (request["edge_references"] as JArray)?.Count ?? 0;
            int faceCount = (request["face_references"] as JArray)?.Count ?? 0;
            if (edgeCount + faceCount == 0)
                return "name the edges: edge_references (stable references of edges, from operation=read host_id=N) and/or " +
                       "face_references (every edge of those faces is taken).";
            return null;
        }

        private static string CheckList(JObject request, string field, int max)
        {
            JToken token = request[field];
            if (token == null) return null;
            if (!(token is JArray list) || list.Count == 0 || list.Count > max || list.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)))
                return field + " must be a list of 1.." + max + " non-empty stable-reference strings.";
            if (list.Select(t => (string)t).Distinct(StringComparer.Ordinal).Count() != list.Count)
                return field + " names the same reference twice.";
            return null;
        }

        /// <summary>
        /// Whether a created sweep sits at its host: its box and the host's box touch, within a tolerance (feet).
        /// A sweep hosted on an edge cannot be away from the element that carries that edge.
        /// </summary>
        public static bool BoxesTouch(double[] aMin, double[] aMax, double[] bMin, double[] bMax, double toleranceFeet)
        {
            if (aMin == null || aMax == null || bMin == null || bMax == null) return false;
            for (int i = 0; i < 3; i++)
                if (aMin[i] > bMax[i] + toleranceFeet || bMin[i] > aMax[i] + toleranceFeet) return false;
            return true;
        }

        /// <summary>A face's role from its normal: "top" and "bottom" for a flat face, else "side" (or "sloped").</summary>
        public static string FaceRole(double nx, double ny, double nz)
        {
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) return "unknown";
            nz /= len;
            if (nz >= 0.999) return "top";
            if (nz <= -0.999) return "bottom";
            if (Math.Abs(nz) <= 0.001) return "side";
            return nz > 0 ? "sloped_up" : "sloped_down";
        }
    }
}
