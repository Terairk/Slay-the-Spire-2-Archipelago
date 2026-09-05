namespace StS2AP.Data
{
    /// <summary>
    /// Defines the 10,000-ID block layout shared by character items and locations.
    /// Character items use one-based blocks; character locations use zero-based blocks.
    /// </summary>
    public static class ArchipelagoIdCodec
    {
        public const long BlockSize = 10000L;

        public static bool IsUniversalItemId(long itemId)
        {
            return itemId >= 0 && itemId < BlockSize;
        }

        public static bool IsCharacterItemId(long itemId)
        {
            return itemId >= BlockSize;
        }

        public static long GetCharacterItemTypeId(long itemId)
        {
            return itemId % BlockSize;
        }

        public static long GetAPCharacterNumberFromItemId(long itemId)
        {
            return itemId / BlockSize;
        }

        public static long GetBaseLocationId(long locationId)
        {
            return locationId % BlockSize;
        }

        public static bool TryComposeLocationId(
            long baseLocationId,
            long apCharacterNumber,
            out long locationId
        )
        {
            if (
                baseLocationId < 0
                || baseLocationId >= BlockSize
                || apCharacterNumber < 1
            )
            {
                locationId = -1;
                return false;
            }

            locationId = ((apCharacterNumber - 1) * BlockSize) + baseLocationId;
            return true;
        }
    }
}
