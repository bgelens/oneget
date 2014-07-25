// 
//  Copyright (c) Microsoft Corporation. All rights reserved. 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  

namespace Microsoft.PowerShell.OneGet.CmdLets {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;
    using System.Threading.Tasks;
    using Microsoft.OneGet.Extensions;
    using Microsoft.OneGet.Packaging;
    using Microsoft.OneGet.Providers.Package;

    [Cmdlet(VerbsLifecycle.Install, PackageNoun, SupportsShouldProcess = true)]
    public sealed class InstallPackage : CmdletWithSearchAndSource {
        private readonly HashSet<string> _sourcesTrusted = new HashSet<string>();

        public InstallPackage()
            : base(new[] {
                OptionCategory.Provider, OptionCategory.Source, OptionCategory.Package, OptionCategory.Install
            }) {
        }

        [Parameter(Mandatory = true, ValueFromPipeline = true, Position = 0, ParameterSetName = PackageByObjectSet)]
        public SoftwareIdentity[] Package {get; set;}

        [Parameter]
        public SwitchParameter Force {get; set;}

        public override bool BeginProcessingAsync() {
            return true;
        }

        public override bool ProcessRecordAsync() {
            if (IsPackageByObject) {
                return InstallPackages(Package);
            }
            return true;
        }

        public override bool EndProcessingAsync() {
            if (IsPackageByObject) {
                // we should have handled these already.
                // buh-bye
                return true;
            }

            // first, determine the package list that we're interested in installing
            var noMatchNames = new HashSet<string>(Name);
            var resultsPerName = new Dictionary<string, List<SoftwareIdentity>>();

            Parallel.ForEach(SelectedProviders, provider => {
                try {
                    if (!Name.IsNullOrEmpty()) {
                        foreach (var each in Name) {
                            var name = each;

                            if (FindViaUri(provider, each, (p) => {
                                lock (resultsPerName) {
                                    resultsPerName.GetOrAdd(name, () => new List<SoftwareIdentity>()).Add(p);
                                }
                            })) {
                                noMatchNames.IfPresentRemoveLocked(each);

                                continue;
                            }

                            if (FindViaFile(provider, each, (p) => {
                                lock (resultsPerName) {
                                    resultsPerName.GetOrAdd(name, () => new List<SoftwareIdentity>()).Add(p);
                                }
                            })) {
                                noMatchNames.IfPresentRemoveLocked(each);
                                continue;
                            }

                            if (FindViaName(provider, each, (p) => {
                                lock (resultsPerName) {
                                    resultsPerName.GetOrAdd(name, () => new List<SoftwareIdentity>()).Add(p);
                                }
                            })) {
                                noMatchNames.IfPresentRemoveLocked(each);
                                continue;
                            }

                            // did not find anything on this provider that matches that name
                        }
                    } else {
                        // no package name passed in.
                        if (!FindViaName(provider, string.Empty, (p) => {
                            lock (resultsPerName) {
                                resultsPerName.GetOrAdd("", () => new List<SoftwareIdentity>()).Add(p);
                            }
                        })) {
                            // nothing found?
                            Warning("No Packages Found -- (no package names/criteria listed)");
                        }
                    }
                } catch (Exception e) {
                    e.Dump();
                }
            });

            if (noMatchNames.Any()) {
                // whine about things not matched.
                foreach (var name in noMatchNames) {
                    Error("No Package Found {0}", name );
                }

                // not going to install.
                return false;
            }

            var failing = false;
            foreach (var set in resultsPerName.Values.Where(set => set.Count > 1)) {
                failing = true;
                // bad, matched too many packages
                string searchKey = null;
                
                foreach (var pkg in set) {
                    Warning(" '{0}' matches multiple packages '{1}/{2}' ({3})",pkg.SearchKey, pkg.Name,pkg.Version,pkg.ProviderName);
                    searchKey = pkg.SearchKey;
                }
                Error("DISAMBIGUATE_FOR_INSTALL", searchKey);
            }

            if (failing) {
                return false;
            }

            // good list. Let's roll...
            var packagesToInstall = resultsPerName.Values.SelectMany(each => each).ToArray();

            return InstallPackages(packagesToInstall);
        }

        private bool InstallPackages(params SoftwareIdentity[] packagesToInstall) {

            foreach (var pkg in packagesToInstall) {
                // if (!WhatIf) {
                //  WriteMasterProgress("Installing", 1, "Installing package '{0}' ({1} of {2})", pkg.Name, n++, packagesToInstall.Length);
                // }
                var provider = SelectProviders(pkg.ProviderName).FirstOrDefault();
                if (provider == null) {
                    Error("UNKNOWN_PROVIDER", pkg.ProviderName);
                    return false;
                }
                try {
                    foreach (var installedPkg in CancelWhenStopped(provider.InstallPackage(pkg, this))) {
                        if (IsCancelled()) {
                            // if we're stopping, just get out asap.
                            return false;
                        }
                        WriteObject(installedPkg);
                    }
                } catch (Exception e) {
                    e.Dump();
                    Error("Installation Failure {0}", pkg.Name );
                    return false;
                }
            }

            return true;
        }

        public override bool ShouldProcessPackageInstall(string packageName, string version, string source) {
            try {
                return Force || ShouldProcess(packageName, "Install Package").Result;
            } catch {
            }
            return false;
        }

        public override bool ShouldContinueWithUntrustedPackageSource(string package, string packageSource) {
            try {
                if (_sourcesTrusted.Contains(packageSource) || Force) {
                    return true;
                }
                if (ShouldContinue("WARNING: This package source is not marked as safe. Are you sure you want to install software from '{0}'".format(packageSource), "Installing Package '{0}' from untrusted source".format(package)).Result) {
                    _sourcesTrusted.Add(packageSource);
                    return true;
                }
            } catch {
            }
            return false;
        }

        public override bool ShouldContinueAfterPackageInstallFailure(string packageName, string version, string source) {
            try {
                return Force || ShouldContinue("Continue Installing after failing '{0}'".format(packageName), "Package Install Failure").Result;
            } catch {
            }
            return false;
        }

        public override bool ShouldContinueRunningInstallScript(string packageName, string version, string source, string scriptLocation) {
            try {
                return Force || ShouldContinue("Should the package script at '{0}' be executed?".format(scriptLocation), "Package Contains Installation Script").Result;
            } catch {
            }
            return false;
        }
    }
}