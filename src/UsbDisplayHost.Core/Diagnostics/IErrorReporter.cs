namespace UsbDisplayHost.Core.Diagnostics;

public interface IErrorReporter
{
    // 错误报告不得因诊断存储不可用而再次抛出普通异常。
    ErrorReport Report(Exception exception, string stage);
    ErrorReport Report(Exception exception, string stage, DiagnosticContext context) => Report(exception, stage);
}
