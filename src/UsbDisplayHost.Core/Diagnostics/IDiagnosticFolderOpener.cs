using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Diagnostics;

public interface IDiagnosticFolderOpener
{
    Task<OperationResult<Unit>> OpenAsync(CancellationToken cancellationToken);
}
