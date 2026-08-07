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

        // CCD and NVAPI disagree on endianness for this field depending on the
        // Windows build, so both byte orders have to be considered.
        //
        // "decodes to three A–Z letters" is NOT enough to pick between them: AOC
        // encodes to 0x05E3, and the byte swap 0xE305 also decodes to letters
        // ("XXE") — which is exactly what users were shown. Bit 15 is the reserved
        // bit and is 0 in every well-formed EDID, so it is the real discriminator;
        // a candidate with the top bit set cannot be the correct reading.
        ushort swappedId = (ushort)((edidManufacturerId << 8) | (edidManufacturerId >> 8));

        var asIs = Decode(edidManufacturerId);
        var swapped = Decode(swappedId);

        bool asIsValid = IsAllLetters(asIs) && (edidManufacturerId & 0x8000) == 0;
        bool swappedValid = IsAllLetters(swapped) && (swappedId & 0x8000) == 0;

        // Only one reading is well-formed — unambiguous.
        if (asIsValid && !swappedValid) return asIs;
        if (swappedValid && !asIsValid) return swapped;

        // Both well-formed (e.g. "DEL" ↔ "IAP"): a code we actually know beats one
        // we don't. Falls through to the raw field order when neither is known,
        // which is what the hardware reported.
        if (asIsValid && swappedValid)
        {
            if (Brands.ContainsKey(asIs)) return asIs;
            if (Brands.ContainsKey(swapped)) return swapped;
            return asIs;
        }

        // Neither is well-formed — take whichever at least looks like letters.
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
