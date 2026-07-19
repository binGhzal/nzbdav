# NZBDav frontend

This directory contains the React Router frontend and its private backend-proxy
runtime. Use the repository-level [contributor guide](../CONTRIBUTING.md) for
the supported dependency, development, build, test, and combined-container
commands.

The standalone frontend image is not a supported V1 deployment. V1 ships one
Docker-first `role=all` container so the frontend/backend key, loopback backend,
SQLite ownership, startup checks, and public protocol policy share one control
boundary.
