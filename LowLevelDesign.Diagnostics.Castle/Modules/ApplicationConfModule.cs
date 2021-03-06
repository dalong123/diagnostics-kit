﻿/**
 *  Part of the Diagnostics Kit
 *
 *  Copyright (C) 2016  Sebastian Solnica
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.

 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 */

using FluentValidation;
using LowLevelDesign.Diagnostics.Castle.Config;
using LowLevelDesign.Diagnostics.Commons.Models;
using LowLevelDesign.Diagnostics.LogStore.Commons.Config;
using LowLevelDesign.Diagnostics.LogStore.Commons.Models;
using LowLevelDesign.Diagnostics.LogStore.Commons.Storage;
using Nancy;
using Nancy.ModelBinding;
using Nancy.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LowLevelDesign.Diagnostics.Castle.Modules
{
    public class ApplicationConfModule : NancyModule
    {
        public ApplicationConfModule(GlobalConfig globals, IAppConfigurationManager appconf, ILogStore logStore, 
            IValidator<Application> appvalidator, IValidator<ApplicationServerConfig> appconfvalidator)
        {
            if (globals.IsAuthenticationEnabled())
            {
                this.RequiresAuthentication();
            }

            Post["conf/appname", true] = async (x, ct) => {
                return await UpdateAppPropertiesAsync(appconf, appvalidator, this.Bind<Application>(), "Name");
            };
            Post["conf/appmaintenance", true] = async (x, ct) => {
                return await UpdateAppPropertiesAsync(appconf, appvalidator, this.Bind<Application>(), "DaysToKeepLogs");
            };
            Post["conf/appexclusion", true] = async (x, ct) => {
                return await UpdateAppPropertiesAsync(appconf, appvalidator, this.Bind<Application>(), "IsExcluded");
            };
            Post["conf/apphidden", true] = async (x, ct) => {
                // we will mark it as excluded also
                var app = this.Bind<Application>();
                app.IsExcluded = true;
                return await UpdateAppPropertiesAsync(appconf, appvalidator, app, new[] { "IsHidden", "IsExcluded" });
            };
            Get["conf/appsrvconfig/{apppath?}", true] = async (x, ct) => {
                IEnumerable<Application> apps;
                if (x.apppath != null) {
                    apps = new[] { await appconf.FindAppAsync(Application.GetPathFromBase64Key((String)x.apppath)) };
                } else {
                    // get all non-hidden applications
                    apps = (await appconf.GetAppsAsync()).Where(app => !app.IsHidden);
                }
                // and send back their configuration
                return await appconf.GetAppConfigsAsync(apps.Select(app => app.Path).ToArray());
            };
            Get["conf/appsrvconfigs", true] = async (x, ct) => {
                var activeAppPaths = (await appconf.GetAppsAsync()).Where(
                    app => !app.IsHidden && !app.IsExcluded).Select(app => app.Path).ToArray();
                return Response.AsJson(await appconf.GetAppConfigsAsync(activeAppPaths));
            };
        }

        private static async Task<String> UpdateAppPropertiesAsync(IAppConfigurationManager appconf,
            IValidator<Application> validator, Application app, params String[] properties)
        {
            var validationResult = validator.Validate(app, properties);
            if (!validationResult.IsValid) {
                return "ERR_INVALID";
            }
            await appconf.UpdateAppPropertiesAsync(app, properties);

            return "OK";
        }
    }
}
