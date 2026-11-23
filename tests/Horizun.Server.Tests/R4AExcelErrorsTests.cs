using System;
using System.IO;
using Horizun.Server;
using Xunit;
public class R4AExcelErrorsTests
{
    [Theory]
    [InlineData("horizun_excel_read_rows")]
    [InlineData("horizun_excel_write_rows")]
    public void MissingWorkbookHasAccessibleFileCode(string tool)
    {
        var detail = HostFailure.Detail(tool, new FileNotFoundException("Workbook not found: example.xlsx"), 600000);
        Assert.Equal("archivo_no_accesible", (string)detail["error_code"]);
    }
    [Fact]
    public void MissingWorksheetHasElementCode()
    {
        var detail = HostFailure.Detail("horizun_excel_write_rows", new InvalidDataException("No worksheet named 'Example'."), 600000);
        Assert.Equal("elemento_no_encontrado", (string)detail["error_code"]);
    }
}
