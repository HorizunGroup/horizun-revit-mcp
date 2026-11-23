using Horizun.Revit.Core;
using Xunit;

public class RoomTagRulesTests
{
    [Fact]
    public void RoomTagMustShowAllThreeApprovedFields()
    {
        Assert.True(RoomTagRules.ContainsFields("101\r\nDormitorio\r\n18,25 m²", "101", "Dormitorio", "18,25 m²"));
        Assert.False(RoomTagRules.ContainsFields("101 Dormitorio", "101", "Dormitorio", "18,25 m²"));
        Assert.False(RoomTagRules.ContainsFields("101 18,25 m²", "101", "Dormitorio", "18,25 m²"));
        Assert.False(RoomTagRules.ContainsFields("2101 Dormitorio 18,25 m²", "101", "Dormitorio", "18,25 m²"));
    }

    [Fact]
    public void FamilyCandidateMustBeAnExactRoomTagName()
    {
        Assert.True(RoomTagRules.IsDefaultFamilyName("M_Room Tag.rfa"));
        Assert.True(RoomTagRules.IsDefaultFamilyName("Room Tag.rfa"));
        Assert.False(RoomTagRules.IsDefaultFamilyName("Room Tag - Number Only.rfa"));
        Assert.False(RoomTagRules.IsDefaultFamilyName("M_Room Tag.rvt"));
    }

    [Fact]
    public void SpanishTemplateTagConcatenationStillProvesAllThreeRoomFields()
    {
        Assert.True(RoomTagRules.ContainsConcatenatedRoomFields(
            "Habitación 1127.596 m²", "1", "Habitación 1", 27.5962));
        Assert.False(RoomTagRules.ContainsConcatenatedRoomFields(
            "Habitación 127.596 m²", "1", "Habitación 1", 27.5962));
        Assert.False(RoomTagRules.ContainsConcatenatedRoomFields(
            "Habitación 1129.596 m²", "1", "Habitación 1", 27.5962));
        Assert.False(RoomTagRules.ContainsConcatenatedRoomFields(
            "Sala 1127.596 m²", "1", "Habitación 1", 27.5962));
    }
}
