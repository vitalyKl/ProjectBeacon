namespace ProjectBeacon.OpsAgent;

// Docker log file access intentionally does not run in OpsAgent.
// The only component that talks to Docker Engine is the isolated DockerLogProxy;
// OpsAgent has neither a Docker socket nor a host Docker filesystem mount.
