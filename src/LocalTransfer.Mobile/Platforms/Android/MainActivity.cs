using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace LocalTransfer.Mobile;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	Exported = true,
	LaunchMode = LaunchMode.SingleTop,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
	                       ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		EnqueueSharedFiles(Intent);
	}

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		if (intent is null)
		{
			return;
		}

		Intent = intent;
		EnqueueSharedFiles(intent);
	}

	private void EnqueueSharedFiles(Intent? intent)
	{
		if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple))
		{
			return;
		}

		var uris = GetSharedUris(intent)
			.GroupBy(uri => uri.ToString(), StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();
		if (uris.Length == 0)
		{
			return;
		}

		SharedFileInbox.Add(uris.Select(CreateSharedFile));
	}

	private static IEnumerable<Android.Net.Uri> GetSharedUris(Intent intent)
	{
		if (intent.ClipData is { } clipData)
		{
			for (var index = 0; index < clipData.ItemCount; index++)
			{
				if (clipData.GetItemAt(index)?.Uri is { } uri)
				{
					yield return uri;
				}
			}
		}

		if (intent.Action == Intent.ActionSend && GetSingleUri(intent) is { } singleUri)
		{
			yield return singleUri;
		}

		if (intent.Action == Intent.ActionSendMultiple)
		{
			foreach (var uri in GetMultipleUris(intent))
			{
				yield return uri;
			}
		}
	}

	private static Android.Net.Uri? GetSingleUri(Intent intent)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			return intent.GetParcelableExtra(
				Intent.ExtraStream,
				Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;
		}

#pragma warning disable CS0618
		return intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
#pragma warning restore CS0618
	}

	private static IEnumerable<Android.Net.Uri> GetMultipleUris(Intent intent)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			return intent.GetParcelableArrayListExtra(
				Intent.ExtraStream,
				Java.Lang.Class.FromType(typeof(Android.Net.Uri)))?.OfType<Android.Net.Uri>()
				?? [];
		}

#pragma warning disable CS0618
		return intent.GetParcelableArrayListExtra(Intent.ExtraStream)?.OfType<Android.Net.Uri>() ?? [];
#pragma warning restore CS0618
	}

	private SharedSourceFile CreateSharedFile(Android.Net.Uri uri)
	{
		var displayName = uri.LastPathSegment ?? "共享文件";
		long? length = null;
		using (var cursor = ContentResolver?.Query(
		           uri,
		           new[] { IOpenableColumns.DisplayName, IOpenableColumns.Size },
		           null,
		           null,
		           null))
		{
			if (cursor?.MoveToFirst() == true)
			{
				var nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
				if (nameIndex >= 0 && !cursor.IsNull(nameIndex))
				{
					displayName = cursor.GetString(nameIndex) ?? displayName;
				}

				var sizeIndex = cursor.GetColumnIndex(IOpenableColumns.Size);
				if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex))
				{
					length = cursor.GetLong(sizeIndex);
				}
			}
		}

		var contentResolver = ContentResolver
			?? throw new InvalidOperationException("Android content resolver is unavailable.");
		var contentType = contentResolver.GetType(uri) ?? "application/octet-stream";
		return new SharedSourceFile(
			displayName,
			contentType,
			length,
			cancellationToken =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Stream stream = contentResolver.OpenInputStream(uri)
					?? throw new FileNotFoundException("The shared Android content URI cannot be opened.");
				return Task.FromResult(stream);
			});
	}
}
