namespace EdgeGateway.Model;

public static class TagDataTypes
{
    public static bool IsString(TagDataType type) => type is TagDataType.AsciiString or TagDataType.Utf8String or TagDataType.Gb2312String;
    public static bool IsStructured(TagDataType type) => type is TagDataType.Any or TagDataType.Custom1 or TagDataType.Custom2 or TagDataType.Custom3 or TagDataType.Custom4 or TagDataType.Custom5;
    public static bool IsNumeric(TagDataType type) => !IsString(type) && !IsStructured(type) && type is not TagDataType.DateTime and not TagDataType.Boolean;
}
