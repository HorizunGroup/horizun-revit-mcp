// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    /// <summary>The vendor of every extensible-storage schema the add-in writes.</summary>
    public static class HorizunVendor
    {
        /// <summary>
        /// EXACTLY the VendorId in Horizun.addin. Revit will not let an add-in write a
        /// vendor-scoped schema registered to anybody else.
        /// </summary>
        public const string Id = "HRZN";
    }
}
