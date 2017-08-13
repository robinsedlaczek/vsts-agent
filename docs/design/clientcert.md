# Support Ssl Client Certificate in Build/Release Job (TFS On-Prem Only)

## Goals

  - Support agent configure and connect to TFS use ssl client certificate
  - Support get source in Build job and download artifact in Release job works with ssl client certificate
  - Provide documentation and scripts to help customer prepare all pre-requrements before configuration
  - Expose ssl client certificate information in vsts-task-lib for task author

## Pre-requirements

  - CA root certificate in `.pem` format (This should contains the public key and signature of the CA root certificate)  
  - Client certificate in `.pem` format (This should contains the public key and signature of the Client certificate)  
  - Client certificate private key in `.pem` format (This should contains only the private key of the Client certificate)  
  - Client certificate archive package in `.pfx` format (This should contains the signature, public key and private key of the Client certificate)  
  - Use `SAME` password to protect Client certificate private key and Client certificate archive package, since they both have client certificate's private key  
  
The Build/Release agent is just xplat tool runner, base on what user defined in their Build/Release definition, invoke different tools to finish user's job. So the client certificate support is not only for the agent infrastructure but most important for all different tools and technologies user might use during a Build/Release job.
```
   Ex:
      Clone Git repository from TFS use Git
      Sync TFVC repository from TFS use Tf.exe on Windows and Tf on Linux/OSX
      Write customer Build/Release task that make REST call to TFS use VSTS-Task-Lib (PowerShell or Node.js)
      Consume Nuget/NPM packages from TFS package management use Nuget.exe and Npm
      Publish and consume artifacts from TFS artifact service use Drop.exe (artifact) and PDBSTR.exe (symbol)
```

You can use `OpenSSL` to get all pre-required certificates format ready easily as long as you have all pieces of information.

#### Windows

    Windows has a pretty good built-in certificate manger, the `Windows Certificate Manager`, it will make most Windows based application deal with certificate problem easily. However, most Linux background application (Git) and technologies (Node.js) won't check the `Windows Certificate Manager`, they just expect all certificates are just a file on disk.  
    1. Export CA certificate from `Trusted CA Store`, use `Base64 Encoding X.509 (.CER)` format.
    2. 
    
In short:  
Agent version 2.121.0 or above  
  - Pass `--proxyurl`, `--proxyusername` and `--proxypassword` during agent configuration.   
    Ex:
    ```
    ./config.cmd --proxyurl http://127.0.0.1:8888 --proxyusername "1" --proxypassword "1"
    ```
    We store your proxy credential securely on each platform.  
    Ex:
    ```
      Windows: Windows Credential Store
      OSX: OSX Keychain
      Linux: Encrypted with symmetric key based on machine id
    ```
  - Create a `.proxybypass` file under agent root to specify proxy bypass Url's Regex (ECMAScript syntax).  
    Ex:
    ```
    github\.com
    bitbucket\.com
    ```
Before 2.121.0
  - Create a `.proxy` file under agent root to specify proxy url.  
    Ex:
    ```
    http://127.0.0.1:8888
    ```
  - For authenticate proxy set environment variables `VSTS_HTTP_PROXY_USERNAME` and `VSTS_HTTP_PROXY_PASSWORD` for proxy credential before start agent process.
  - Create a `.proxybypass` file under agent root to specify proxy bypass Url's Regex (ECMAScript syntax).  
    Ex:
    ```
    github\.com
    bitbucket\.com
    ```

## How agent handle proxy within a Build/Release job

After configuring proxy for agent, agent infrastructure will start talk to VSTS/TFS service through the web proxy specified in the `.proxy` file.  

Since the code for `Get Source` step in build job and `Download Artifact` step in release job are also bake into agent, those steps will also follow the agent proxy configuration from `.proxy` file.  

Agent will expose proxy configuration via environment variables for every task execution, task author need to use `vsts-task-lib` methods to retrieve back proxy configuration and handle proxy with their task.

## Get proxy configuration by using [VSTS-Task-Lib](https://github.com/Microsoft/vsts-task-lib) method

Please reference [VSTS-Task-Lib doc](https://github.com/Microsoft/vsts-task-lib/blob/master/node/docs/proxy.md) for detail
