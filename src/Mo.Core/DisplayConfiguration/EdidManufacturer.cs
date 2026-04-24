namespace Mo.Core.DisplayConfiguration;

// Decodes the 16-bit EDID manufacturer ID (3×5-bit PNP code) into a 3-letter
// code, and maps common codes to friendly brand names.
public static class EdidManufacturer
{
    // Curated list of the most common monitor/TV manufacturers. Unknown codes
    // fall back to the raw 3-letter PNP ID so users still see *something*.
    private static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GSM"] = "LG",
        ["LGD"] = "LG Display",
        ["LGE"] = "LG Electronics",
        ["SAM"] = "Samsung",
        ["SEC"] = "Samsung",
        ["SDC"] = "Samsung Display",
        ["DEL"] = "Dell",
        ["APP"] = "Apple",
        ["AUS"] = "ASUS",
        ["ASU"] = "ASUS",
        ["MSI"] = "MSI",
        ["BNQ"] = "BenQ",
        ["ACI"] = "Acer",
        ["ACR"] = "Acer",
        ["LEN"] = "Lenovo",
        ["HWP"] = "HP",
        ["HPN"] = "HP",
        ["HPQ"] = "HP",
        ["VSC"] = "ViewSonic",
        ["VIZ"] = "Vizio",
        ["SNY"] = "Sony",
        ["AOC"] = "AOC",
        ["GIG"] = "Gigabyte",
        ["IVM"] = "Iiyama",
        ["EIZ"] = "EIZO",
        ["HEI"] = "Hyundai",
        ["PHL"] = "Philips",
        ["PNS"] = "Panasonic",
        ["NEC"] = "NEC",
        ["XMI"] = "Xiaomi",
        ["HUA"] = "Huawei",
        ["HAI"] = "Haier",
        ["RZR"] = "Razer",
        ["CMN"] = "Innolux",
        ["BOE"] = "BOE",
        ["AUO"] = "AU Optronics",
        ["CMO"] = "Chi Mei",
        ["SHP"] = "Sharp",
    };

    // EDID packs the manufacturer code into a big-endian 16-bit value where
    // each letter takes 5 bits (A=1..Z=26). Windows CCD returns this as
    // EdidManufacturerId. The high bit (bit 15) is always 0 in well-formed EDIDs.
    public static string GetPnpId(ushort edidManufacturerId)
    {
        if (edidManufacturerId == 0) return string.Empty;

        // Try both byte orders; the one that decodes to all A–Z wins. CCD and
        // NVAPI disagree on endianness for this field depending on the Windows
        // build, so we accept whichever interpretation yields a valid PNP ID.
        var asIs = Decode(edidManufacturerId);
        var swapped = Decode((ushort)((edidManufacturerId << 8) | (edidManufacturerId >> 8)));

        if (IsAllLetters(asIs)) return asIs;
        if (IsAllLetters(swapped)) return swapped;
        return asIs; // Best effort.

        static string Decode(ushort v)
        {
            int a = (v >> 10) & 0x1F;
            int b = (v >> 5) & 0x1F;
            int c = v & 0x1F;
            return string.Concat((char)('A' + a - 1), (char)('A' + b - 1), (char)('A' + c - 1));
        }

        static bool IsAllLetters(string s) => s.Length == 3 && s.All(ch => ch is >= 'A' and <= 'Z');
    }

    public static string? GetBrandName(ushort edidManufacturerId)
    {
        var pnp = GetPnpId(edidManufacturerId);
        if (string.IsNullOrEmpty(pnp)) return null;
        return Brands.TryGetValue(pnp, out var name) ? name : pnp;
    }
}
