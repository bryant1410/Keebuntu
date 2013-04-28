using System;
using System.Diagnostics;
using System.Threading;
using System.Drawing.Imaging;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using KeePass.Plugins;
using KeePassLib;
using Keebuntu.Dbus;
using DBus;

namespace KeebuntuAppMenu
{
  public class KeebuntuAppMenuExt : Plugin
  {
    const string menuPath = "/com/canonical/menu/{0}";

    private IPluginHost mPluginHost;
    private Thread mGtkBusThread;
    private MenuStripDBusMenu mDBusMenu;
    private object mStartupThreadLock = new object();

    public override bool Initialize(IPluginHost host)
    {
      mPluginHost = host;
      Monitor.Enter(mStartupThreadLock);
      try {
        mGtkBusThread = new Thread(RunGtkDBusThread);
        mGtkBusThread.SetApartmentState(ApartmentState.STA);
        mGtkBusThread.Name = "KeebuntuAppMenu DBus Thread";
        mGtkBusThread.Start();
        if (!Monitor.Wait(mStartupThreadLock, 5000) || !mGtkBusThread.IsAlive) {
          mGtkBusThread.Abort();
          throw new Exception("KeebuntuAppMenu DBus thread failed to start");
        }
        // mimmic behavior of other ubuntu apps
        if (Environment.GetEnvironmentVariable("APPMENU_DISPLAY_BOTH") != "1")
        {
          mPluginHost.MainWindow.MainMenu.Visible = false;
        }
      } catch (Exception ex) {
        Debug.Fail(ex.ToString());
        return false;
      } finally {
        Monitor.Exit(mStartupThreadLock);
      }
      return true;
    }

    public override void Terminate()
    {
      try {
        InvokeGtkThread(() => Gtk.Application.Quit());
      } catch (Exception ex) {
        Debug.Fail(ex.ToString());
      }
    }

    private void RunGtkDBusThread()
    {
      Monitor.Enter(mStartupThreadLock);
      try {

        BusG.Init();
        Gtk.Application.Init();

        /* setup ApplicationMenu */

        mDBusMenu = new MenuStripDBusMenu(mPluginHost.MainWindow.MainMenu);

        var sessionBus = Bus.Session;

#if DEBUG
        const string dbusBusPath = "/org/freedesktop/DBus";
        const string dbusBusName = "org.freedesktop.DBus";
        var dbusObjectPath = new ObjectPath(dbusBusPath);
        var dbusService =
          sessionBus.GetObject<org.freedesktop.DBus.IBus>(dbusBusName, dbusObjectPath);
        dbusService.NameAcquired += (name) => Console.WriteLine ("NameAcquired: " + name);
#endif
        const string registrarBusPath = "/com/canonical/AppMenu/Registrar";
        const string registratBusName = "com.canonical.AppMenu.Registrar";
        var registrarObjectPath = new ObjectPath(registrarBusPath);
        var unityPanelServiceBus =
          sessionBus.GetObject<com.canonical.AppMenu.IRegistrar>(registratBusName,
                                                                 registrarObjectPath);
        var mainFormXid = GetWindowXid(mPluginHost.MainWindow);
        var mainFormObjectPath = new ObjectPath(string.Format(menuPath,
                                                              mainFormXid));
        sessionBus.Register(mainFormObjectPath, mDBusMenu);
        unityPanelServiceBus.RegisterWindow((uint)mainFormXid.ToInt32(),
                                            mainFormObjectPath);

        // have to re-register the window each time the main windows is shown
        // otherwise we lose the application menu
        mPluginHost.MainWindow.Activated += (sender, e) =>
        {
          // TODO - sometimes we invoke this unnessasarily. If there is a way to
          // test that we are still registered, that would proably be better.
          // For now, it does not seem to hurt anything.
          InvokeGtkThread(
            () => unityPanelServiceBus.RegisterWindow((uint)mainFormXid.ToInt32(),
                                                      mainFormObjectPath));
        };

        Monitor.Pulse(mStartupThreadLock);
        Monitor.Exit(mStartupThreadLock);

        /* run gtk event loop */
        Gtk.Application.Run();

      } catch (Exception ex) {
        Debug.Fail(ex.ToString());
        Monitor.Pulse(mStartupThreadLock);
        Monitor.Exit(mStartupThreadLock);
      }
    }

    private IntPtr GetWindowXid(System.Windows.Forms.Form form)
    {
      var typeName = typeof(System.Windows.Forms.Control).AssemblyQualifiedName;
      var hwndTypeName = typeName.Replace("Control", "Hwnd");
      var hwndType = Type.GetType(hwndTypeName);
      var objectFromHandleMethod =
        hwndType.GetMethod("ObjectFromHandle", BindingFlags.Public | BindingFlags.Static);
      var hwnd =
        objectFromHandleMethod.Invoke(null, new object[] { form.Handle });
      var wholeWindowField = hwndType.GetField("whole_window",
                                               BindingFlags.NonPublic | BindingFlags.Instance);
      return (IntPtr)wholeWindowField.GetValue(hwnd);
    }

    private void InvokeMainWindow(Action action)
    {
      var mainWindow = mPluginHost.MainWindow;
      if (mainWindow.InvokeRequired) {
        mainWindow.Invoke(action);
      } else {
        action.Invoke();
      }
    }

    private void InvokeGtkThread(Action action)
    {
      if (ReferenceEquals(Thread.CurrentThread, mGtkBusThread)) {
        action.Invoke();
      } else {
        Gtk.ReadyEvent readyEvent = () => action.Invoke();
        var threadNotify = new Gtk.ThreadNotify(readyEvent);
        threadNotify.WakeupMain();
      }
    }
  }
}

