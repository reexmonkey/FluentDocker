﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Ductus.FluentDocker.Commands;
using Ductus.FluentDocker.Extensions;
using Ductus.FluentDocker.Model;

namespace Ductus.FluentDocker.Services.Impl
{
  public sealed class DockerHostService : ServiceBase, IHostService
  {
    private const string DockerHost = "DOCKER_HOST";
    private const string DockerCertPath = "DOCKER_CERT_PATH";
    private const string DockerTlsVerify = "DOCKER_TLS_VERIFY";
    private const string DefaultCaCertName = "ca.pem";
    private const string DefaultClientCertName = "cert.pem";
    private const string DefaultClientKeyName = "key.pem";

    private readonly bool _stopWhenDisposed;
    private string _caCertPath;
    private string _clientCertPath;
    private string _clientKeyPath;

    public DockerHostService(string name, bool isNative, bool stopWhenDisposed = false, string dockerUri = null,
      string certificatePath = null)
      : base(name)
    {
      _stopWhenDisposed = stopWhenDisposed;

      IsNative = isNative;
      if (IsNative)
      {
        var uri = dockerUri ?? Environment.GetEnvironmentVariable(DockerHost);
        if (string.IsNullOrEmpty(uri))
        {
          throw new ArgumentException($"DockerHostService cannot be native when {DockerHost} is not defined",
            nameof(isNative));
        }

        var certPath = certificatePath ?? Environment.GetEnvironmentVariable(DockerCertPath);
        if (string.IsNullOrEmpty(certPath))
        {
          throw new ArgumentException($"DockerHostService cannot be native when {DockerCertPath} is not defined",
            nameof(isNative));
        }

        Host = new Uri(uri);
        RequireTls = Environment.GetEnvironmentVariable(DockerTlsVerify) == "1";
        ClientCaCertificate = certPath.ToCertificate(DefaultCaCertName);
        ClientCertificate = certPath.ToCertificate(DefaultClientCertName, DefaultClientKeyName);
        State = ServiceRunningState.Running;

        _caCertPath = Path.Combine(certPath, DefaultCaCertName);
        _clientCertPath = Path.Combine(certPath, DefaultClientCertName);
        _clientKeyPath = Path.Combine(certPath, DefaultClientKeyName);
        return;
      }

      // Machine - do inspect & get url
      MachineSetup(name);
    }

    public override void Dispose()
    {
      if (_stopWhenDisposed && !IsNative)
      {
        Name.Stop();
      }
    }

    public override void Start()
    {
      if (!IsNative)
      {
        throw new InvalidOperationException($"Cannot start docker host {Name} since it is native");
      }

      if (State != ServiceRunningState.Stopped)
      {
        throw new InvalidOperationException($"Cannot start docker host {Name} since it has state {State}");
      }

      var response = Name.Start();
      if (!response.Success)
      {
        throw new InvalidOperationException($"Could not start docker host {Name}");
      }

      if (!IsNative)
      {
        MachineSetup(Name);
      }
    }

    public override void Stop()
    {
      if (!IsNative)
      {
        throw new InvalidOperationException($"Cannot stop docker host {Name} since it is native");
      }

      if (State != ServiceRunningState.Running)
      {
        throw new InvalidOperationException($"Cannot stop docker host {Name} since it has state {State}");
      }

      var response = Name.Stop();
      if (!response.Success)
      {
        throw new InvalidOperationException($"Could not stop docker host {Name}");
      }
    }

    public override void Remove(bool force = false)
    {
      if (!IsNative)
      {
        throw new InvalidOperationException($"Cannot remove docker host {Name} since it is native");
      }

      if (State == ServiceRunningState.Running && !force)
      {
        throw new InvalidOperationException(
          $"Cannot remove docker host {Name} since it has state {State} and force is not enabled");
      }

      var response = Name.Delete(force);
      if (!response.Success)
      {
        throw new InvalidOperationException($"Could not remove docker host {Name}");
      }
    }

    public Uri Host { get; private set; }
    public bool IsNative { get; }
    public bool RequireTls { get; private set; }
    public X509Certificate2 ClientCertificate { get; }
    public X509Certificate2 ClientCaCertificate { get; private set; }

    public IList<IContainerService> RunningContainers => GetContainers(false);

    public IList<IContainerService> GetContainers(bool all = true, string filter = null)
    {
      var options = string.Empty;
      if (all)
      {
        options += " --all";
      }

      if (string.IsNullOrEmpty(filter))
      {
        options += $" --filter={filter}";
      }

      var result = Host.Ps(options, _caCertPath, _clientCertPath, _clientKeyPath);
      if (!result.Success)
      {
        return new List<IContainerService>();
      }

      return (from id in result.Data
        let config = Host.InspectContainer(id, _clientCertPath, _clientCertPath, _clientKeyPath)
        select new DockerContainerService(config.Data.Name, id, Host, new CertificatePaths
        {
          CaCertificate = _caCertPath,
          ClientKey = _clientKeyPath,
          ClientCertificate = _clientCertPath
        })).Cast<IContainerService>().ToList();
    }

    private void MachineSetup(string name)
    {
      State = name.Status();
      if (State == ServiceRunningState.Running)
      {
        return;
      }

      Host = name.Uri();

      var info = name.Inspect().Data;
      RequireTls = info.RequireTls;

      ClientCaCertificate =
        Path.GetDirectoryName(info.AuthConfig.CaCertPath).ToCertificate(Path.GetFileName(info.AuthConfig.CaCertPath));

      ClientCaCertificate =
        Path.GetDirectoryName(info.AuthConfig.ClientCertPath)
          .ToCertificate(Path.GetFileName(info.AuthConfig.ClientCertPath),
            Path.GetFileName(info.AuthConfig.ClientKeyPath));

      _caCertPath = info.AuthConfig.CaCertPath;
      _clientCertPath = info.AuthConfig.ClientCertPath;
      _clientKeyPath = info.AuthConfig.ClientKeyPath;
    }
  }
}