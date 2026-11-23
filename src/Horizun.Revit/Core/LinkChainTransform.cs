using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    // Each matrix maps coordinates from one linked document to its parent.
    public static class LinkChainTransform
    {
        public static bool IsTie(double firstFeet, double secondFeet) => Math.Abs(firstFeet - secondFeet) < 1.0 / 304.8;
        public static double[,] Compose(IEnumerable<double[,]> chain)
        {
            var result = new double[4, 4];
            for (int i = 0; i < 4; i++) result[i, i] = 1;
            foreach (var next in chain)
            {
                if (next == null || next.GetLength(0) != 4 || next.GetLength(1) != 4)
                    throw new ArgumentException("Each link transform must be 4 by 4.");
                var product = new double[4, 4];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                    for (int k = 0; k < 4; k++) product[i, j] += result[i, k] * next[k, j];
                result = product;
            }
            return result;
        }
    }
}
