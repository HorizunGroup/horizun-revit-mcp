namespace Horizun.Revit.Core
{
    public static class LargeModelRules
    {
        public const int ElementThreshold = 100000;
        public static bool? IsLarge(int? elementCount) =>
            elementCount.HasValue ? (bool?)(elementCount.Value >= ElementThreshold) : null;
    }
}
