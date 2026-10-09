using IncidentLab;
using IncidentLab.Api;

if (args is ["--verify"]) Environment.ExitCode = await PipelineVerification.Run();
else await LabApplication.Build(args).RunAsync();

public partial class Program;
