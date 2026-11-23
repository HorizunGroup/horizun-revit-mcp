// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH DRY RUNS MUST BE REHEARSED IN REVIT. Two checks of create_elements are
// measured on Revit's own geometry after the elements exist: the floor/wall
// overlap guard (a new floor or wall crossing a finish floor or a wall) and the
// stairs level span (the stairs' real top past a level). A dry run that only
// checked arguments answered "valid" and the apply then refused, writing
// nothing (5.0.5, measured on a course model). Those batches are now built and
// rolled back during the dry run, so it refuses with the apply's own reason.
// Large batches stay argument-only (the rehearsal would double their time);
// validation_mode=revit_rollback still forces it, and the note says so.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class GeometryRehearsalRules
    {
        public const int MaxAutomaticElements = 200;

        public static bool ShouldRehearse(int elements, bool stairs, bool newFloor, bool newWall,
                                          bool modelHasWalls, bool modelHasFinishFloors)
        {
            if (stairs) return true;                       // a stairs batch is one element
            if (elements <= 0 || elements > MaxAutomaticElements) return false;
            return (newFloor && (modelHasWalls || newWall)) || (newWall && modelHasFinishFloors);
        }

        /// <summary>
        /// A model conflict found on Revit's geometry (floor/wall overlap, stairs past a level) is a precondition the
        /// model did not meet, never a schema error. Without this the code was guessed from the message, and the
        /// guidance's "specify an explicit overlap tolerance" read as esquema_invalido (measured live, 5.0.5).
        /// Null for any other failure, which keeps its usual classification.
        /// </summary>
        public static string ConflictErrorCode(string exceptionMessage)
        {
            if (string.IsNullOrEmpty(exceptionMessage)) return null;
            return exceptionMessage.StartsWith("floor_wall_overlap_", System.StringComparison.Ordinal) ||
                   exceptionMessage.StartsWith("Stairs level_span crosses", System.StringComparison.Ordinal)
                ? "precondicion_no_cumplida" : null;
        }

        public const string SkippedNote =
            "This batch has more than 200 elements, so the floor/wall overlap guard was not rehearsed in Revit: " +
            "it is measured on apply. Send validation_mode=revit_rollback to rehearse it now.";
    }
}
