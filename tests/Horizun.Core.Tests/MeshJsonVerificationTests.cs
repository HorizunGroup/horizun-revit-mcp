using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MeshJsonVerificationTests
    {
        [Theory]
        [InlineData("{\"triangles\":[0,0,0,1,0,0,0,1,0],\"triangle_count\":1}", 1, true)]
        [InlineData("{\"triangles\":[]}", 0, true)]
        [InlineData("{\"triangles\":[0,0,0]}", 1, false)]
        [InlineData("{\"triangles\":[[0,0,0,1,0,0,0,1,0]]}", 1, false)]
        [InlineData("{\"triangles\":[0,0,0,1,0,0,0,1,\"0\"]}", 1, false)]
        [InlineData("{\"triangles\":[0,0,0,1,0,0,0,1,NaN]}", 1, false)]
        [InlineData("{\"triangles\":[],\"triangles\":[]}", 0, false)]
        [InlineData("{\"triangles\":[0,0,0,1,0,0,0,1,0]", 1, false)]
        public void Re_read_verifies_content_and_structure_not_just_file_existence(string json, long count, bool valid)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "mesh-wire-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, json);
                string hash = ExportDeliveryRules.MeshHash(path);
                Assert.Equal(valid, ExportDeliveryRules.VerifyMeshFile(path, count, hash) == null);
                Assert.NotNull(ExportDeliveryRules.VerifyMeshFile(path, count, "different-content-hash"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
