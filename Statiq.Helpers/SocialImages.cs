﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Statiq.Common;
using Statiq.Core;
using Statiq.Web;
using Statiq.Web.Modules;
using Statiq.Web.Pipelines;

namespace Thirty25.Statiq.Helpers
{
    public class SocialImages : Pipeline
    {
        public SocialImages()
        {
            Dependencies.AddRange(nameof(Inputs));

            ProcessModules = new ModuleList
            {
                new GetPipelineDocuments(ContentType.Content),

                // Filter to non-archive content
                new FilterDocuments(Config.FromDocument(doc => !Archives.IsArchive(doc))),

                // Process the content
                new CacheDocuments
                {
                    new AddTitle(),
                    new SetDestination(true),
                    new ExecuteIf(Config.FromSetting(WebKeys.OptimizeContentFileNames, true))
                    {
                        new OptimizeFileName()
                    },
                    new GenerateSocialImage(),
                }
            };

            OutputModules = new ModuleList { new WriteFiles() };
        }
    }

    internal class GenerateSocialImage : ParallelModule
    {
        private WebApplication _app;
        private IPlaywright _playwright;
        private IBrowser _browser;
        private IBrowserContext _context;

        protected override async Task BeforeExecutionAsync(IExecutionContext context)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services
                .AddRazorPages()
                .WithRazorPagesRoot("/Statiq.Helpers");

            _app = builder.Build();
            _app.MapRazorPages();
            _app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(Directory.GetCurrentDirectory(), @"input/assets")),
                RequestPath = new PathString("/assets")
            });
            await _app.StartAsync();

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync();
            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1200, Height = 628 },
            });
            await base.BeforeExecutionAsync(context);
        }

        protected override async Task FinallyAsync(IExecutionContext context)
        {
            await _context.DisposeAsync();
            await _browser.DisposeAsync();
            _playwright.Dispose();
            await _app.DisposeAsync();
            await base.FinallyAsync(context);
        }

        protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(IDocument input,
            IExecutionContext context)
        {
            var url = _app.Urls.First(u => u.StartsWith("http://"));
            var page = await _context.NewPageAsync();

            var title = input.GetString("Title");
            var description = input.GetString("Description");
            var tags = input.GetList<string>("tags") ?? Array.Empty<string>();

            await page.GotoAsync($"{url}/SocialCard?title={title}&desc={description}&tags={string.Join(';', tags)}");

            // This will not just wait for the  page to load over the network, but it'll also give
            // chrome a chance to complete rendering of the fonts while the wait timeout completes.
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
            var bytes = await page.ScreenshotAsync();

            var destination = input.Destination.InsertSuffix("-social").ChangeExtension("png");
            // can we set this property then pull it when rendering the page?
            var doc = context.CreateDocument(
                input.Source,
                destination,
                new MetadataItems { { "DocId", input.Id } },
                context.GetContentProvider(bytes));

            return new[] { doc };
        }
    }
}
