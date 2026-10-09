namespace LocalTransfer.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		HookGlobalExceptionLogging();

		MainPage = new AppShell();
	}

	private static void HookGlobalExceptionLogging()
	{
		// Anything that escapes a Java-initiated callback surfaces as an opaque
		// "java.lang.RuntimeException: exception_wasthrown" with no usable detail, so persist the
		// real shape of the failure for the next real-device attempt.
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			if (args.ExceptionObject is Exception exception)
			{
				MobileDiagnostics.Log("appdomain", exception);
			}
		};

		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			MobileDiagnostics.Log("task", args.Exception);
			args.SetObserved();
		};

#if ANDROID
		Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
			MobileDiagnostics.Log("android", args.Exception);
#endif
	}
}
