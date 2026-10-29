namespace HardwareLens.Windows;

/// <summary>Maps USB vendor IDs (assigned by the USB-IF) to company names. Only IDs listed here are named; unknown IDs are shown as the raw ID.</summary>
internal static class UsbVendors
{
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = "Logitech", ["0FD9"] = "Elgato", ["1E71"] = "NZXT", ["045E"] = "Microsoft", ["1532"] = "Razer", ["1B1C"] = "Corsair", ["1038"] = "SteelSeries", ["05AC"] = "Apple", ["04F2"] = "Chicony", ["0BDA"] = "Realtek",
        ["8087"] = "Intel", ["0A5C"] = "Broadcom", ["04CA"] = "Lite-On", ["413C"] = "Dell", ["03F0"] = "HP", ["17EF"] = "Lenovo", ["0B05"] = "ASUS", ["0951"] = "Kingston", ["0781"] = "SanDisk",
        ["058F"] = "Alcor Micro", ["090C"] = "Silicon Motion", ["04E8"] = "Samsung", ["054C"] = "Sony", ["057E"] = "Nintendo", ["28DE"] = "Valve", ["2DC8"] = "8BitDo", ["046A"] = "Cherry",
        ["0C45"] = "Microdia", ["1BCF"] = "Sunplus", ["0408"] = "Quanta", ["13D3"] = "IMC Networks", ["0489"] = "Foxconn", ["04D9"] = "Holtek", ["258A"] = "SINO WEALTH", ["0416"] = "Winbond",
        ["04B4"] = "Cypress", ["2109"] = "VIA Labs", ["05E3"] = "Genesys Logic", ["0424"] = "Microchip (SMSC)", ["1A40"] = "Terminus Technology", ["0B95"] = "ASIX", ["2357"] = "TP-Link",
        ["0CF3"] = "Qualcomm Atheros", ["148F"] = "Ralink / MediaTek", ["0E0F"] = "VMware", ["80EE"] = "VirtualBox", ["0403"] = "FTDI", ["067B"] = "Prolific", ["10C4"] = "Silicon Labs",
        ["1A86"] = "QinHeng Electronics", ["2341"] = "Arduino", ["04B8"] = "Epson", ["04A9"] = "Canon", ["04F9"] = "Brother", ["043D"] = "Lexmark", ["0482"] = "Kyocera", ["06BC"] = "OKI",
        ["04DA"] = "Panasonic", ["0930"] = "Toshiba", ["1058"] = "Western Digital", ["0BC2"] = "Seagate", ["154B"] = "PNY", ["125F"] = "ADATA", ["0D8C"] = "C-Media", ["1395"] = "Sennheiser",
        ["047F"] = "Poly (Plantronics)", ["0B0E"] = "GN (Jabra)", ["0A12"] = "Cambridge Silicon Radio", ["1235"] = "Focusrite", ["0763"] = "M-Audio", ["041E"] = "Creative", ["0DB0"] = "Micro-Star (MSI)",
        ["1462"] = "Micro-Star (MSI)", ["1E7D"] = "ROCCAT", ["0738"] = "Mad Catz", ["044F"] = "Thrustmaster", ["06A3"] = "Saitek", ["0B38"] = "Gear Head", ["0E8F"] = "GreenAsia", ["24AE"] = "Shenzhen Rapoo",
        ["093A"] = "Pixart Imaging", ["1C4F"] = "SiGma Micro", ["0461"] = "Primax", ["04B3"] = "IBM", ["0ECD"] = "Lite-On", ["2516"] = "Cooler Master", ["3434"] = "Keychron", ["320F"] = "Evision",
        ["0E6F"] = "PDP", ["045B"] = "Hitachi", ["0AC8"] = "Z-Star Microelectronics", ["0C76"] = "JMTek", ["174F"] = "Syntek", ["064E"] = "Suyin", ["13FE"] = "Kingston (Phison)", ["1307"] = "USBest",
        ["18A5"] = "Verbatim", ["0EA0"] = "Ours Technology", ["1D6B"] = "Linux Foundation (virtual hub)", ["0A46"] = "Davicom", ["07CA"] = "AVerMedia", ["1415"] = "Nam Tai (PlayStation Eye)", ["054C"] = "Sony",
        ["0955"] = "NVIDIA", ["10DE"] = "NVIDIA", ["1050"] = "Yubico", ["2C97"] = "Ledger", ["1209"] = "Generic (open hardware)", ["16C0"] = "Van Ooijen Technische Informatica (shared)", ["0483"] = "STMicroelectronics",
        ["0A5C"] = "Broadcom", ["04D8"] = "Microchip Technology", ["2E8A"] = "Raspberry Pi", ["239A"] = "Adafruit", ["1FC9"] = "NXP", ["03EB"] = "Atmel", ["1366"] = "SEGGER",
    };
    public static string? Name(string? vid) => vid != null && Map.TryGetValue(vid, out var n) ? n : null;
}
