namespace Teamprojekt.Ftms
{
    public static class FtmsUuids
    {
        public const string FtmsService = "1826";
        public const string HrService = "180d";
        public const string IndoorBikeData = "2ad2";
        public const string FtmsFeature = "2acc";
        public const string ResistanceRange = "2ad6";
        public const string PowerRange = "2ad8";
        public const string ControlPoint = "2ad9";
        public const string FtmsStatus = "2ada";
        public const string HrMeasurement = "2a37";

        public const int OpRequestControl = 0x00;
        public const int OpSetResistance = 0x04;
        public const int OpSetPower = 0x05;
        public const int OpStart = 0x07;
        public const int OpStop = 0x08;
        public const int OpSetSimulation = 0x11;
        public const int OpResponse = 0x80;
        public const int StopParam = 0x01;
        public const int ResultSuccess = 0x01;

        public static string Short(string uuid)
        {
            if (string.IsNullOrEmpty(uuid))
                return "";
            string text = uuid.ToLowerInvariant();
            const string suffix = "-0000-1000-8000-00805f9b34fb";
            if (text.Length == 36 && text.StartsWith("0000") && text.EndsWith(suffix))
                return text.Substring(4, 4);
            if (text.Length == 8 && text.StartsWith("0000"))
                return text.Substring(4, 4);
            return text;
        }
    }
}
