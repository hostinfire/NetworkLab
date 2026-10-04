# Save and restore work

Use **File > New**, **Open**, **Save**, or **Save As**. Northstar project files use JSON with the `.nslab` extension and include a format version, devices, interfaces, static routes, canvas positions, and links.

The app writes a recovery copy every two minutes to `%LocalAppData%\NorthstarNetworkLab\autosave.nslab`. On the next start it asks whether to restore the recovery copy.

Your Discord Application ID is stored separately in `settings.json` and is not written into a topology project.