﻿namespace CK2Events

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open ElectronNET.API
open ElectronNET.API.Entities
open CK2Events.Application
open Application.Localisation



type Startup private () =
    new (configuration: IConfiguration) as this =
        Startup() then
        this.Configuration <- configuration
    

    // This method gets called by the runtime. Use this method to add services to the container.
    member this.ConfigureServices(services: IServiceCollection) =
        // Add framework services.  
        services.AddMvc() |> ignore
        services.AddOptions() |> ignore
        services.Configure<CK2Settings>(this.Configuration) |> ignore
        services.AddSingleton<IConfiguration>(this.Configuration) |> ignore
        services.AddTransient<LocalisationService>() |> ignore


    member __.ElectronBootstrap() =

        let webprefs = WebPreferences (NodeIntegration = false)
        let prefs = BrowserWindowOptions (WebPreferences = webprefs)
        // webprefs.NodeIntegration <- false
        // prefs.Show <- true
        // prefs.WebPreferences <- webprefs
       // prefs.Fullscreen <- true
        Electron.App.CommandLineAppendArgument("--disable-http-cache")
        Electron.App.CommandLineAppendSwitch("--disable-http-cache")
        let window = Electron.WindowManager.CreateWindowAsync(prefs) |> Async.AwaitTask |> Async.RunSynchronously
        window.Maximize()
        
        //Task.Run((fun _ -> async {Electron.WindowManager.CreateWindowAsync(prefs)  |> ignore} |> Async.RunSynchronously |> ignore)) |> ignore

    

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    member this.Configure(app: IApplicationBuilder, env: IHostingEnvironment) =

        if (env.IsDevelopment() || HybridSupport.IsElectronActive) then
            app.UseDeveloperExceptionPage() |> ignore
        else
            app.UseExceptionHandler("/Home/Error") |> ignore

        app.UseStaticFiles() |> ignore

        app.UseMvcWithDefaultRoute() |> ignore

        if HybridSupport.IsElectronActive then
            this.ElectronBootstrap()
            Electron.App.add_Quitting (fun _ -> Electron.Dialog.ShowMessageBoxAsync("Quitting") |> ignore)

        // app.UseMvc(fun routes ->
        //     routes.MapRoute(
        //         name = "default",
        //         template = "{controller=Home}/{action=Index}/{id?}") |> ignore
        //     ) |> ignore

    member val Configuration : IConfiguration = null with get, set
