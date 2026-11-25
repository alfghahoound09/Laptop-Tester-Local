using System.Text;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class HardwareDetectionTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", GpuKind.Discrete)]
    [InlineData("RTX 4070 Laptop GPU", GpuKind.Discrete)]
    [InlineData("NVIDIA RTX A2000 Laptop GPU", GpuKind.Discrete)]
    [InlineData("NVIDIA Quadro T1000", GpuKind.Discrete)]
    [InlineData("AMD Radeon RX 7600S", GpuKind.Discrete)]
    [InlineData("AMD Radeon Pro W6600M", GpuKind.Discrete)]
    [InlineData("Intel(R) Arc(TM) A370M Graphics", GpuKind.Discrete)]
    [InlineData("Intel(R) Iris(R) Xe MAX Graphics", GpuKind.Discrete)]
    [InlineData("Intel(R) UHD Graphics 620", GpuKind.Integrated)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) Graphics", GpuKind.Integrated)]
    [InlineData("Intel(R) Graphics", GpuKind.Integrated)]
    [InlineData("AMD Radeon(TM) Graphics", GpuKind.Integrated)]
    [InlineData("AMD Radeon 780M Graphics", GpuKind.Integrated)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", GpuKind.Integrated)]
    [InlineData("Qualcomm(R) Adreno(TM) X1-85 GPU", GpuKind.Integrated)]
    [InlineData("Microsoft Basic Display Adapter", GpuKind.Virtual)]
    [InlineData("Microsoft Remote Display Adapter", GpuKind.Virtual)]
    [InlineData("VMware SVGA 3D", GpuKind.Virtual)]
    [InlineData("Microsoft Hyper-V Video", GpuKind.Virtual)]
    public void ClassifyGpu_IdentifiesDiscreteIntegratedAndVirtual(string name, string expectedKind)
    {
        Assert.Equal(expectedKind, HardwareService.ClassifyGpu(name));
    }

    [Fact]
    public void SelectPrimaryGpu_PrefersDiscreteOverIntegratedOverVirtual()
    {
        var igpu = new GpuDetails("Intel(R) Iris(R) Xe Graphics", "1.0", 0.5, GpuKind.Integrated, "1920x1080");
        var dgpu = new GpuDetails("NVIDIA GeForce RTX 4060 Laptop GPU", "551.0", 8.0, GpuKind.Discrete, "Active");
        var vgpu = new GpuDetails("Microsoft Basic Display Adapter", "10.0", 0, GpuKind.Virtual, "Active");

        Assert.Equal(dgpu, HardwareService.SelectPrimaryGpu([igpu, dgpu]));
        Assert.Equal(dgpu, HardwareService.SelectPrimaryGpu([dgpu, igpu]));
        Assert.Equal(igpu, HardwareService.SelectPrimaryGpu([vgpu, igpu]));
        Assert.Equal(vgpu, HardwareService.SelectPrimaryGpu([vgpu]));
        Assert.Null(HardwareService.SelectPrimaryGpu([]));
    }

    [Fact]
    public void BuildGpuSummary_FormatsHybridHidesVirtualWhenRealExists()
    {
        var igpu = new GpuDetails("Intel(R) Graphics", "32.0", 1.0, GpuKind.Integrated, "1920x1200");
        var dgpu = new GpuDetails("NVIDIA GeForce RTX 4060", "551.0", 8.0, GpuKind.Discrete, "Active");
        var vgpu = new GpuDetails("Microsoft Basic Display Adapter", "10.0", 0, GpuKind.Virtual, "Active");

        Assert.Equal("Unknown GPU", HardwareService.BuildGpuSummary([]));
        Assert.Equal("NVIDIA GeForce RTX 4060 (dGPU) + Intel(R) Graphics (iGPU)",
            HardwareService.BuildGpuSummary([igpu, vgpu, dgpu]));
        Assert.Equal("Microsoft Basic Display Adapter (Virtual)",
            HardwareService.BuildGpuSummary([vgpu]));
    }

    [Fact]
    public void BuildGpuDetails_HybridIntelAndNvidiaLaptop_HandlesOptimusHeadlessDgpuAndTrueVram()
    {
        var wmiRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "Intel(R) Iris(R) Xe Graphics",
                ["DriverVersion"] = "31.0.101.5186",
                ["AdapterRAM"] = 1073741824UL, // 1 GB
                ["CurrentHorizontalResolution"] = 1920,
                ["CurrentVerticalResolution"] = 1200
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "NVIDIA GeForce RTX 4060 Laptop GPU",
                ["DriverVersion"] = "32.0.15.6094",
                ["AdapterRAM"] = 4293918720UL, // 4 GB WMI uint32 cap
                ["CurrentHorizontalResolution"] = 0, // Optimus routes panel through iGPU
                ["CurrentVerticalResolution"] = 0
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "Parsec Virtual Display Adapter",
                ["DriverVersion"] = "0.41.0.0",
                ["AdapterRAM"] = 0UL
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "   " // ignored blank entry
            }
        };

        var regVram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["NVIDIA GeForce RTX 4060 Laptop GPU"] = 7.96
        };

        var gpus = HardwareService.BuildGpuDetails(wmiRows, regVram);

        Assert.Equal(3, gpus.Count);
        Assert.Equal(GpuKind.Integrated, gpus[0].AdapterType);
        Assert.Equal("1920x1200", gpus[0].Resolution);
        Assert.Equal(1.0, gpus[0].VramGb);

        Assert.Equal(GpuKind.Discrete, gpus[1].AdapterType);
        Assert.Equal("Active", gpus[1].Resolution);
        Assert.Equal(7.96, gpus[1].VramGb);

        Assert.Equal(GpuKind.Virtual, gpus[2].AdapterType);
        Assert.Equal(gpus[1], HardwareService.SelectPrimaryGpu(gpus));
        Assert.Equal(
            "NVIDIA GeForce RTX 4060 Laptop GPU (dGPU) + Intel(R) Iris(R) Xe Graphics (iGPU)",
            HardwareService.BuildGpuSummary(gpus));
    }

    [Theory]
    [InlineData("AMD Radeon 780M Graphics", "AMD Radeon RX 7600S")]
    [InlineData("Intel(R) Arc(TM) Graphics", "Intel(R) Arc(TM) A370M Graphics")]
    public void HybridPairs_SameVendorIgpuAndDgpu_AreBothPreservedWithDgpuFirstInSummary(string igpuName, string dgpuName)
    {
        var wmiRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase) { ["Name"] = igpuName, ["CurrentHorizontalResolution"] = 2560, ["CurrentVerticalResolution"] = 1600 },
            new(StringComparer.OrdinalIgnoreCase) { ["Name"] = dgpuName }
        };

        var gpus = HardwareService.BuildGpuDetails(wmiRows, new Dictionary<string, double>());

        Assert.Equal(2, gpus.Count);
        Assert.Equal(GpuKind.Integrated, gpus[0].AdapterType);
        Assert.Equal(GpuKind.Discrete, gpus[1].AdapterType);
        Assert.Equal(gpus[1], HardwareService.SelectPrimaryGpu(gpus));
        Assert.Equal($"{dgpuName} (dGPU) + {igpuName} (iGPU)", HardwareService.BuildGpuSummary(gpus));
    }

    [Fact]
    public void ParseRegistryVramGb_HandlesInt64ByteArraysAndInvalidValues()
    {
        Assert.Equal(7.96, HardwareService.ParseRegistryVramGb(8546942976L));
        Assert.Equal(8.0, HardwareService.ParseRegistryVramGb(BitConverter.GetBytes(8UL * 1024 * 1024 * 1024)));
        Assert.Equal(2.0, HardwareService.ParseRegistryVramGb(BitConverter.GetBytes(2U * 1024 * 1024 * 1024)));
        Assert.Equal(0.0, HardwareService.ParseRegistryVramGb(null));
        Assert.Equal(0.0, HardwareService.ParseRegistryVramGb(-1L));
    }

    [Theory]
    [InlineData(0x09E5, "BOE Technology")] // B(2), O(15), E(5)
    [InlineData(0x30E4, "LG Display")]     // L(12), G(7), D(4)
    [InlineData(0x5A5A, "VRZ")]            // V(22), R(18), Z(26)
    public void DecodeEdidManufacturer_DecodesThreeLetterCodes(ushort raw, string expected)
    {
        Assert.Equal(expected, HardwareService.DecodeEdidManufacturer(raw));
    }

    [Fact]
    public void ParseEdidDescriptorName_And_ParseEdid_WorkAtOffsets54And72()
    {
        byte[] shortEdid = new byte[64];
        Assert.Null(HardwareService.ParseEdid(shortEdid, "FALLBACK"));

        byte[] edid = new byte[128];
        edid[8] = 0x09;
        edid[9] = 0xE5; // BOE
        edid[72] = 0x00;
        edid[73] = 0x00;
        edid[75] = 0xFC;
        Encoding.ASCII.GetBytes("NE140WUM-N62\n").CopyTo(edid, 72 + 5);

        Assert.Equal("NE140WUM-N62", HardwareService.ParseEdidDescriptorName(edid));
        var parsed = HardwareService.ParseEdid(edid, "GENERIC");
        Assert.NotNull(parsed);
        Assert.Equal("BOE Technology", parsed!.Value.Mfg);
        Assert.Equal("NE140WUM-N62", parsed.Value.Model);

        byte[] noNameEdid = new byte[128];
        noNameEdid[8] = 0x30;
        noNameEdid[9] = 0xE4;
        Assert.Equal(string.Empty, HardwareService.ParseEdidDescriptorName(noNameEdid));
        Assert.Equal(("LG Display", "LEN403D"), HardwareService.ParseEdid(noNameEdid, "LEN403D"));
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1920, 1200, "16:10")]
    [InlineData(2560, 1600, "16:10")]
    [InlineData(0, 1080, "")]
    [InlineData(1920, 0, "")]
    public void ComputeAspectRatio_ReturnsExpectedRatio(int w, int h, string expected)
    {
        Assert.Equal(expected, HardwareService.ComputeAspectRatio(w, h));
    }

    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, "RAM")]
    public void DecodeSmbiosMemoryType_MapsExpectedCodes(int code, string expected)
    {
        Assert.Equal(expected, HardwareService.DecodeSmbiosMemoryType(code));
    }

    [Theory]
    [InlineData("VMware, Inc.", "VMware20,1", "Phoenix", true)]
    [InlineData("innotek GmbH", "VirtualBox", "Oracle", true)]
    [InlineData("QEMU", "Standard PC", "SeaBIOS", true)]
    [InlineData("Microsoft Corporation", "Virtual Machine", "Hyper-V", true)]
    [InlineData("LENOVO", "ThinkPad T14 Gen 6", "LENOVO", false)]
    public void DetectVirtualMachine_IdentifiesHypervisors(string mfg, string model, string bios, bool expected)
    {
        Assert.Equal(expected, HardwareService.DetectVirtualMachine(mfg, model, bios));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-1165G7 CPU @ 2.80GHz", "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", true)]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", "Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz", false)]
    [InlineData("AMD Ryzen 7 7840U w/ Radeon 780M Graphics   ", "AMD Ryzen 7 7840U w/ Radeon 780M Graphics", true)]
    [InlineData("", "AMD Ryzen 5", true)]
    public void CpuNamesMatch_NormalizesAndCompares(string a, string b, bool expected)
    {
        Assert.Equal(expected, AntiFakeService.CpuNamesMatch(a, b));
    }
}
