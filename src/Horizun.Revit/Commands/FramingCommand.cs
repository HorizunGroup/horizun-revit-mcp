// -----------------------------------------------------------------------------
// Horizun Revit MCP - light-gauge / drywall framing from existing walls and ceilings.
// Original Horizun code.
//
// WHAT THIS TOOL IS. A consultancy models, from wall-type and ceiling DETAILS (often
// received as images or 2-D DWG details), the framing the finished wall or ceiling
// hides: studs, tracks, kings, jacks, headers, sills, cripples, blocking; and for a
// suspended drywall ceiling its hangers, main channels, furring (cross) channels and
// perimeter track. The tool NEVER reads an image: the MCP client (a vision-capable
// model, guided by the prompt framing-from-detail) fills a typed spec, the person
// confirms it, and this command builds exactly that spec and re-reads it. Families,
// sizes, spacings and rules are the caller's data; nothing organisation-specific is
// compiled in.
//
// THE SPLIT. Where each member goes is Revit-free and unit-tested
// (Core/WallFramingRules.cs, Core/CeilingFramingRules.cs). This file is the
// dispatcher; the Revit halves live beside it in their own partial files (wall
// reading and member placement, ceiling boundary and hanger ray-casts, the marker
// that read/remove and idempotence rely on).
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class FramingCommand : ICommand
    {
        public string Name => "horizun_framing";
        public string Description =>
            "Build light-gauge/drywall framing (studs, tracks, openings, blocking; ceiling mains, cross, perimeter, hangers) from a caller spec, verified.";

        private static readonly string[] Operations = { "wall", "ceiling", "read", "remove" };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(Operations, op) < 0)
                return CommandResult.Fail("operation must be wall, ceiling, read or remove. Nothing was written.");

            if (op == "read") return ReadFraming(app, request);
            return ApplyFraming(app, request, op);
        }
    }
}
