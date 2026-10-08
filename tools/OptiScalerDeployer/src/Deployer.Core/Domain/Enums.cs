namespace Deployer.Core.Domain;

/// <summary>游戏来源平台。决定身份 DLL 的候选优先级。</summary>
public enum GamePlatform { Xbox, Steam, Epic, Manual }

/// <summary>部署目录的可写性探测结果。</summary>
public enum DirectoryWritability { Writable, NeedsElevation, Blocked, Unknown }

/// <summary>OptiScaler 可以伪装的 DLL 身份。顺序无意义，优先级在 IdentityContext 的候选表里。</summary>
public enum DllIdentity { Dxgi, Winmm, Version, Dbghelp, D3d12, Wininet, Winhttp, Asi }

public enum IdentityStatus { Resolved, RequiresUserDecision, NoCandidateAvailable }

/// <summary>为什么要把某个原件收进影子备份。</summary>
public enum BackupReason { OverwriteGameFile, OverwritePreviousOpti, IniModified }

public enum ElevationRequirement { NotRequired, Required, Declined, Blocked }

public enum WarningSeverity { Info, Caution, Blocking }

public enum PayloadOrigin { LocalBuild, GitHubRelease, ManualDirectory }

/// <summary>游戏使用的图形 API。一个游戏可能同时用多个（UE 常见 DX12 + Vulkan）。</summary>
public enum GraphicsApi { Dx11, Dx12, Vulkan }

/// <summary>游戏内已存在的超分 SDK。决定 OptiScaler 该开哪些 Input。</summary>
public enum UpscalerSdk { Dlss, Streamline, XeSS, Fsr2, Fsr3, FfxApi }

public enum InstallState { Installed, Healthy, RolledBack, NeedsReinstall, Failed }
