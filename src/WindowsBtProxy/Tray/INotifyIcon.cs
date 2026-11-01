namespace WindowsBtProxy.Tray;

/// <summary>
/// The tray icon as <see cref="TrayController"/> sees it, so the polling rules can be tested without a shell. The real one is
/// <see cref="Win32NotifyIcon"/>; every call comes from the UI thread.
/// </summary>
internal interface INotifyIcon
{
    /// <summary>Puts the icon in the notification area (at start, and again after Explorer restarts and forgets it).</summary>
    void Add(TrayView view);

    /// <summary>Changes the colour and tooltip of the icon that is already there.</summary>
    void Update(TrayView view);

    /// <summary>Takes the icon out of the notification area; nothing happens when it is not there.</summary>
    void Remove();
}
