using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

// Rhino reads the *assembly* Guid as PlugIn.Id when the class attribute alone is not enough
// (common with SDK-style projects where AssemblyInfo is auto-generated without a Guid).
[assembly: Guid("E8C3A1F2-4B7D-4E9A-8C1F-3D6E9B0A2C5D")]

[assembly: PlugInDescription(DescriptionType.Organization, "UrbanThat")]
[assembly: PlugInDescription(DescriptionType.Email, "")]
[assembly: AssemblyTitle("UrbanBridgePlugin")]
[assembly: AssemblyDescription("Rhino ↔ web/Unreal bridge + road network graph")]
[assembly: AssemblyCompany("UrbanThat")]
[assembly: AssemblyProduct("UrbanBridge")]
[assembly: ComVisible(false)]
