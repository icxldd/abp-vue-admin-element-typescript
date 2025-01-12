﻿using LINGYUN.Abp.Serilog.Enrichers.Application;
using LINGYUN.MicroService.Internal.ApiGateway.Localization;
using LINGYUN.MicroService.Internal.ApiGateway.Ocelot.Configuration.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Ocelot.Configuration.Repository;
using Ocelot.DependencyInjection;
using Ocelot.Multiplexer;
using Ocelot.Provider.Polly;
using StackExchange.Redis;
using System;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Volo.Abp.Caching;
using Volo.Abp.Json;
using Volo.Abp.Json.SystemTextJson;
using Volo.Abp.Localization;
using Volo.Abp.VirtualFileSystem;

namespace LINGYUN.MicroService.Internal.ApiGateway
{
    public partial class InternalApiGatewayModule
    {
        private void PreConfigureApp()
        {
            AbpSerilogEnrichersConsts.ApplicationName = "Internal-ApiGateWay";
        }

        private void ConfigureApiGateway(IConfiguration configuration)
        {
            Configure<InternalApiGatewayOptions>(configuration.GetSection("ApiGateway"));
        }


        private void ConfigureKestrelServer(IConfiguration configuration, bool isDevelopment = true)
        {
            // fix: 不限制请求体大小，解决上传文件问题
            Configure<KestrelServerOptions>(options =>
            {
                options.Limits.MaxRequestBodySize = null;
                options.Limits.MaxRequestBufferSize = null;
            });

            if (!isDevelopment)
            {
                // Ssl证书
                var sslOptions = configuration.GetSection("App:SslOptions");
                if (sslOptions.Exists())
                {
                    var fileName = sslOptions["FileName"];
                    var password = sslOptions["Password"];
                    Configure<KestrelServerOptions>(options =>
                    {
                        options.ConfigureEndpointDefaults(cfg =>
                        {
                            cfg.UseHttps(fileName, password);
                        });
                    });
                }
            }
        }

        private void ConfigureJsonSerializer()
        {
            // 统一时间日期格式
            Configure<AbpJsonOptions>(options =>
            {
                options.DefaultDateTimeFormat = "yyyy-MM-dd HH:mm:ss";
            });
            // 中文序列化的编码问题
            Configure<AbpSystemTextJsonSerializerOptions>(options =>
            {
                options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
            });
        }

        private void ConfigureVirtualFileSystem()
        {
            Configure<AbpVirtualFileSystemOptions>(options =>
            {
                options.FileSets.AddEmbedded<InternalApiGatewayModule>("LINGYUN.MicroService.Internal.ApiGateway");
            });
        }

        private void ConfigureLocalization()
        {
            Configure<AbpLocalizationOptions>(options =>
            {
                options.Languages.Add(new LanguageInfo("en", "en", "English"));
                options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "简体中文"));

                options.Resources
                    .Add<ApiGatewayResource>()
                    .AddVirtualJson("/Localization/Resources");
            });
        }

        private void ConfigureMvc(IServiceCollection services)
        {
            var mvcBuilder = services.AddMvc();
            mvcBuilder.AddApplicationPart(typeof(InternalApiGatewayModule).Assembly);

            Configure<AbpEndpointRouterOptions>(options =>
            {
                options.EndpointConfigureActions.Add(endpointContext =>
                {
                    endpointContext.Endpoints.MapControllerRoute("defaultWithArea", "{area}/{controller=Home}/{action=Index}/{id?}");
                    endpointContext.Endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
                });
            });
        }

        private void ConfigureOcelot(IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IFileConfigurationRepository, DiskFileConfigurationAggragatorRepository>();
            services
                .AddOcelot(configuration)
                .AddPolly()
                .AddSingletonDefinedAggregator<AbpResponseMergeAggregator>();
        }

        private void ConfigureCaching(IConfiguration configuration)
        {
            Configure<AbpDistributedCacheOptions>(options =>
            {
                // 最好统一命名,不然某个缓存变动其他应用服务有例外发生
                options.KeyPrefix = "LINGYUN.Abp.Application";
                // 滑动过期30天
                options.GlobalCacheEntryOptions.SlidingExpiration = TimeSpan.FromDays(30d);
                // 绝对过期60天
                options.GlobalCacheEntryOptions.AbsoluteExpiration = DateTimeOffset.Now.AddDays(60d);
            });

            Configure<RedisCacheOptions>(options =>
            {
                var redisConfig = ConfigurationOptions.Parse(options.Configuration);
                options.ConfigurationOptions = redisConfig;
                options.InstanceName = configuration["Redis:InstanceName"];
            });
        }

        private void ConfigureSwagger(IServiceCollection services)
        {
            services.AddSwaggerGen(
                options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Open API Document", Version = "v1" });
                    options.DocInclusionPredicate((docName, description) => true);
                    options.CustomSchemaIds(type => type.FullName);
                    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                    {
                        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                        Name = "Authorization",
                        In = ParameterLocation.Header,
                        Scheme = "bearer",
                        Type = SecuritySchemeType.Http,
                        BearerFormat = "JWT"
                    });
                    options.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                            },
                            new string[] { }
                        }
                    });
                });
        }

        private void ConfigureSecurity(IServiceCollection services, IConfiguration configuration, bool isDevelopment = false)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = configuration["AuthServer:Authority"];
                    options.RequireHttpsMetadata = false;
                    options.Audience = configuration["AuthServer:ApiName"];
                });

            if (!isDevelopment)
            {
                var redis = ConnectionMultiplexer.Connect(configuration["Redis:Configuration"]);
                services
                    .AddDataProtection()
                    .SetApplicationName("LINGYUN.Abp.Application")
                    .PersistKeysToStackExchangeRedis(redis, "LINGYUN.Abp.Application:DataProtection:Protection-Keys");
            }
        }
    }
}
