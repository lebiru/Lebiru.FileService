# Fetch Functionality

## Overview

The Fetch functionality allows users to pull files from external sources directly into Lebiru.FileService. Instead of manually downloading files from various services and then uploading them, users can configure "Fetch Sources" that connect to external services and retrieve files automatically.

## Supported External Sources

Currently, Lebiru.FileService supports the following external sources:

- **Gmail** - Fetch attachments from emails in your Gmail account
- **Dropbox** *(Coming Soon)* - Fetch files from your Dropbox account
- **WebURL** *(Coming Soon)* - Fetch files from direct web URLs

## Managing Fetch Sources

Fetch Sources are user-defined connections to external services. Each user can manage their own set of Fetch Sources.

### Adding a New Fetch Source

1. Navigate to the "Fetch Sources" section in the sidebar
2. Click the "Add New Fetch Source" button
3. Select the source type (Gmail, Dropbox, WebURL)
4. Provide the required authentication information
5. Give your Fetch Source a friendly name (e.g., "Work Gmail", "Personal Dropbox")
6. Click "Save" to create the Fetch Source

### Editing a Fetch Source

1. In the "Fetch Sources" list, find the source you want to edit
2. Click the "Edit" button next to the source
3. Update the configuration as needed
4. Click "Save" to apply your changes

### Removing a Fetch Source

1. In the "Fetch Sources" list, find the source you want to remove
2. Click the "Remove" button next to the source
3. Confirm deletion when prompted

### Testing a Fetch Source Connection

Before using a Fetch Source, you can test the connection to ensure it works properly:

1. In the "Fetch Sources" list, find the source you want to test
2. Click the "Test Connection" button
3. The system will attempt to connect to the external service
4. You'll see a success message if the connection works, or an error message with details if it fails

## Using Fetch Sources

### Gmail Fetch Source

The Gmail Fetch Source allows you to retrieve email attachments based on email subject filters.

#### Configuration Options

- **Account**: Your Gmail account (requires OAuth authentication)
- **Subject Filter**: Keywords to search for in email subjects
- **Attachment Type Filter** *(Optional)*: Filter by file extension (e.g., PDF, DOCX)
- **Max Age** *(Optional)*: Only consider emails received within a specific timeframe (e.g., last 7 days)

#### How It Works

When executed, the Gmail Fetch Source:

1. Connects to your Gmail account using the stored credentials
2. Searches for emails matching your subject filter
3. Identifies the most recent matching email
4. Downloads all attachments from that email (or just those matching the attachment type filter)
5. Imports the attachments into your Lebiru.FileService files

#### Example Use Cases

- Automatically fetch weekly reports sent via email
- Import monthly invoices or statements
- Retrieve documents forwarded to your email

### Executing a Fetch

To execute a fetch operation:

1. Navigate to the "Fetch Now" section
2. Select the Fetch Source you want to use
3. Optionally refine the fetch parameters (specific to the source type)
4. Click "Fetch Now" to execute
5. The system will display progress and results of the fetch operation

## Security Considerations

- Fetch Sources store the minimum credentials needed to access the external service
- OAuth is used where possible to avoid storing passwords
- All credentials are encrypted in the database
- Users can only access and use their own Fetch Sources
- Connection credentials are validated during both setup and execution

## Future Enhancements

The Fetch functionality is designed to be extensible. Planned future enhancements include:

- **Dropbox Integration**: Connect to your Dropbox account to import files
- **WebURL Integration**: Import files from direct web URLs
- **Google Drive Integration**: Connect to Google Drive to import files
- **OneDrive Integration**: Connect to Microsoft OneDrive to import files
- **Scheduled Fetches**: Configure fetches to run automatically on a schedule
- **Advanced Filtering**: More complex rules for selecting which files to fetch
- **Batch Processing**: Process multiple emails or sources in a single fetch operation

## Troubleshooting

Common issues and their resolutions:

| Issue | Possible Solution |
|-------|------------------|
| Authentication failures | Re-authenticate the Fetch Source by editing it and updating credentials |
| No files found | Check the filter criteria to ensure they match the expected content |
| Fetch operation times out | Try again later, or check if the external service is experiencing issues |
| File size limitations | Large files may be rejected. Check the maximum file size setting in your account |

For additional assistance, please contact system support.

---

*Last updated: September 27, 2025*