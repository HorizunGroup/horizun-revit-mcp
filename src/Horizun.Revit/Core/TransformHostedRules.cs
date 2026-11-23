namespace Horizun.Revit.Core
{
    /// <summary>
    /// Field case (telemetry 2026-11-09): rotating or mirroring a wall-hosted instance (a door or window)
    /// to face the other side either rolled back ("cannot mirror an instance without its host") or committed
    /// with the axes unchanged, both reported as error_interno after the apply. Revit keeps a wall-hosted
    /// instance's orientation tied to its host, so the request is refused before the transaction with the
    /// way that does work: flip the instance (facing or hand), not turn it.
    /// </summary>
    public static class TransformHostedRules
    {
        public const string Code = "instancia_alojada_en_muro";

        /// <summary>The refusal text, or null when the operation is not blocked by hosting.</summary>
        public static string Refusal(string operation, long elementId, bool isFamilyInstance, bool hostIsWall)
        {
            if (!isFamilyInstance || !hostIsWall) return null;
            if (operation != "rotate" && operation != "mirror") return null;
            return "ElementId " + elementId + " is a family instance hosted on a wall (a door, window or other wall-hosted family): " +
                   "Revit keeps its orientation tied to the wall, so " + operation + " either rolls back or leaves its axes unchanged. " +
                   "To make it face the other side of the wall flip it instead (FamilyInstance.flipFacing(); flipHand() for the swing side): " +
                   "no typed command flips an existing instance, so this is the case for horizun_execute_python with a script that " +
                   "re-reads FacingFlipped/HandFlipped. To turn it in plan, move or rotate its host wall. Nothing was written. " +
                   "(" + Code + ")";
        }
    }
}
