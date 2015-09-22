﻿using Microsoft.Extensions.Logging;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Features;
using Orchard.Environment.Extensions.Models;
using Orchard.Environment.Extensions.Utility;
using Orchard.Events;
using Orchard.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orchard.DisplayManagement.Descriptors {
    public class DefaultShapeTableManager : IShapeTableManager {
        private readonly IEnumerable<IShapeTableProvider> _bindingStrategies;
        private readonly IExtensionManager _extensionManager;
        private readonly IFeatureManager _featureManager;
        private readonly IEventBus _eventBus;
        private readonly ITypeFeatureProvider _typeFeatureProvider;
        private readonly ILogger _logger;

        public DefaultShapeTableManager(
            IEnumerable<IShapeTableProvider> bindingStrategies,
            IExtensionManager extensionManager,
            IFeatureManager featureManager,
            IEventBus eventBus,
            ITypeFeatureProvider typeFeatureProvider,
            ILoggerFactory loggerFactory) {
            _bindingStrategies = bindingStrategies;
            _extensionManager = extensionManager;
            _featureManager = featureManager;
            _eventBus = eventBus;
            _typeFeatureProvider = typeFeatureProvider;
            _logger = loggerFactory.CreateLogger<DefaultShapeTableManager>();
        }

        public ShapeTable GetShapeTable(string themeName) {
            _logger.LogInformation("Start building shape table");

            IList<IReadOnlyList<ShapeAlteration>> alterationSets = new List<IReadOnlyList<ShapeAlteration>>();
            foreach (var bindingStrategy in _bindingStrategies) {
                Feature strategyDefaultFeature = 
                    _typeFeatureProvider.GetFeatureForDependency(bindingStrategy.GetType());

                var builder = new ShapeTableBuilder(strategyDefaultFeature);
                bindingStrategy.Discover(builder);
                alterationSets.Add(builder.BuildAlterations().ToReadOnlyCollection());
            }

            var alterations = alterationSets
            .SelectMany(shapeAlterations => shapeAlterations)
            .Where(alteration => IsModuleOrRequestedTheme(alteration, themeName))
            .OrderByDependenciesAndPriorities(AlterationHasDependency, GetPriority)
            .ToList();

            var descriptors = alterations.GroupBy(alteration => alteration.ShapeType, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Aggregate(
                    new ShapeDescriptor { ShapeType = group.Key },
                    (descriptor, alteration) => {
                        alteration.Alter(descriptor);
                        return descriptor;
                    })).ToList();

            foreach (var descriptor in descriptors) {
                foreach (var alteration in alterations.Where(a => a.ShapeType == descriptor.ShapeType).ToList()) {
                    var local = new ShapeDescriptor { ShapeType = descriptor.ShapeType };
                    alteration.Alter(local);
                    descriptor.BindingSources.Add(local.BindingSource);
                }
            }

            var result = new ShapeTable {
                Descriptors = descriptors.ToDictionary(sd => sd.ShapeType, StringComparer.OrdinalIgnoreCase),
                Bindings = descriptors.SelectMany(sd => sd.Bindings).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };
            
            //await _eventBus.NotifyAsync<IShapeTableEventHandler>(x => x.ShapeTableCreated(result));

            _logger.LogInformation("Done building shape table");
            return result;
        }

        private static int GetPriority(ShapeAlteration shapeAlteration) {
            return shapeAlteration.Feature.Descriptor.Priority;
        }

        private bool AlterationHasDependency(ShapeAlteration item, ShapeAlteration subject) {
            return _extensionManager.HasDependency(item.Feature.Descriptor, subject.Feature.Descriptor);
        }

        private bool IsModuleOrRequestedTheme(ShapeAlteration alteration, string themeName) {
            if (alteration == null ||
                alteration.Feature == null ||
                alteration.Feature.Descriptor == null ||
                alteration.Feature.Descriptor.Extension == null) {
                return false;
            }

            var extensionType = alteration.Feature.Descriptor.Extension.ExtensionType;
            if (DefaultExtensionTypes.IsModule(extensionType)) {
                return true;
            }

            if (DefaultExtensionTypes.IsTheme(extensionType)) {
                // alterations from themes must be from the given theme or a base theme
                var featureName = alteration.Feature.Descriptor.Id;
                return string.IsNullOrEmpty(featureName) || featureName == themeName || IsBaseTheme(featureName, themeName);
            }

            return false;
        }

        private bool IsBaseTheme(string featureName, string themeName) {
            // determine if the given feature is a base theme of the given theme
            var availableFeatures = _extensionManager.AvailableFeatures();

            var themeFeature = availableFeatures.SingleOrDefault(fd => fd.Id == themeName);
            while (themeFeature != null) {
                var baseTheme = themeFeature.Extension.BaseTheme;
                if (string.IsNullOrEmpty(baseTheme)) {
                    return false;
                }
                if (featureName == baseTheme) {
                    return true;
                }
                themeFeature = availableFeatures.SingleOrDefault(fd => fd.Id == baseTheme);
            }
            return false;
        }
    }
}
