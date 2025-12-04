using Core;
using Core.Interfaces;
using ModbusSlave.Services;
using ModbusSlave.ViewModels;
using ModbusSlave.Views;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;
using System.Windows;
using Windows;
using Windows.ViewModels;
using Windows.Views;

namespace ModbusSlave
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }
        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {   
            containerRegistry.Register<Slave>(); //当我们想让自己的类通过容器的注入构造时,在容器中注册这个类,并使用容器的resolve函数创建此类
            containerRegistry.RegisterSingleton<IModbusSlave,TcpModbusSlave>();
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            RegionManager regionManager = Container.Resolve<RegionManager>();
            regionManager.RegisterViewWithRegion("MainRegion","Slaves");
        }

        protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
        {
            moduleCatalog.AddModule<WindowsModule>();
            moduleCatalog.AddModule<CoreModule>();
        }
    }
}
