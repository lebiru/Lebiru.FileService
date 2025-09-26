/**
 * MIME type validator for Lebiru.FileService
 * Provides functions to validate file MIME types for security
 */

const MimeValidator = {
    // List of allowed MIME types (can be expanded as needed)
    allowedMimeTypes: [
        // Documents
        'application/pdf',
        'application/msword',
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document', // .docx
        'application/vnd.ms-excel',
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', // .xlsx
        'application/vnd.ms-powerpoint',
        'application/vnd.openxmlformats-officedocument.presentationml.presentation', // .pptx
        'text/plain',
        'text/csv',
        'application/rtf',
        'application/zip',
        'application/x-rar-compressed',
        'application/x-7z-compressed',
        
        // Images
        'image/jpeg',
        'image/png',
        'image/gif',
        'image/bmp',
        'image/webp',
        'image/svg+xml',
        'image/tiff',
        
        // Audio
        'audio/mpeg',
        'audio/wav',
        'audio/ogg',
        'audio/webm',
        
        // Video
        'video/mp4',
        'video/webm',
        'video/ogg',
        'video/quicktime',
        
        // Other common formats
        'application/json',
        'text/html',
        'text/css',
        'application/javascript',
        'application/xml',
        'text/xml'
    ],
    
    // Known risky MIME types that should be blocked
    riskyMimeTypes: [
        'application/x-msdownload', // .exe
        'application/x-ms-installer', // .msi
        'application/x-sh', // .sh
        'application/x-csh', // .csh
        'application/x-bat', // .bat
        'application/x-cmd', // .cmd
        'application/java-archive', // .jar
        'application/x-javascript', // .js as executable
        'application/vnd.microsoft.portable-executable', // .exe
        'application/x-dosexec', // DOS executables
        'application/vnd.apple.installer+xml', // .mpkg
        'application/vnd.ms-cab-compressed', // .cab
        'application/x-httpd-php', // .php
        'text/x-php', // Another PHP variant
        'application/x-perl', // .pl
        'application/x-python', // .py as executable
        'application/x-ruby' // .rb as executable
    ],
    
    /**
     * Validates a file's MIME type
     * @param {File} file - The file object to validate
     * @returns {Promise<{valid: boolean, message: string, mimeType: string}>}
     */
    validateFile: function(file) {
        return new Promise((resolve) => {
            // First check by file extension for common risky files
            const fileName = file.name.toLowerCase();
            const extension = fileName.substring(fileName.lastIndexOf('.') + 1);
            const riskyExtensions = ['exe', 'bat', 'cmd', 'sh', 'php', 'dll', 'msi', 'jar'];
            
            if (riskyExtensions.includes(extension)) {
                resolve({
                    valid: false,
                    message: `File type "${extension}" is not allowed for security reasons`,
                    mimeType: file.type
                });
                return;
            }
            
            // Use FileReader to validate the file contents against its MIME type
            const reader = new FileReader();
            
            reader.onload = (event) => {
                const arr = new Uint8Array(event.target.result).subarray(0, 4);
                let header = "";
                for (let i = 0; i < arr.length; i++) {
                    header += arr[i].toString(16);
                }
                
                // Validate file signature against common formats
                const fileType = this.getFileTypeFromSignature(header);
                const declaredType = file.type;
                
                // If the file has a recognized signature but it doesn't match the declared type
                if (fileType && !this.isMimeTypeConsistent(fileType, declaredType)) {
                    resolve({
                        valid: false,
                        message: `File "${file.name}" has mismatched content type`,
                        mimeType: `Declared: ${declaredType}, Actual: ${fileType}`
                    });
                    return;
                }
                
                // Check if the MIME type is in the allowed list
                if (this.allowedMimeTypes.includes(declaredType)) {
                    resolve({
                        valid: true,
                        message: "File type is allowed",
                        mimeType: declaredType
                    });
                    return;
                }
                
                // Check if the MIME type is in the risky list
                if (this.riskyMimeTypes.includes(declaredType)) {
                    resolve({
                        valid: false,
                        message: `File type "${declaredType}" is not allowed for security reasons`,
                        mimeType: declaredType
                    });
                    return;
                }
                
                // If it's neither explicitly allowed nor risky, default to rejection
                // This can be changed to allow unknown types by setting to true
                resolve({
                    valid: false,
                    message: `Unknown file type "${declaredType}" is not allowed`,
                    mimeType: declaredType
                });
            };
            
            reader.onerror = () => {
                resolve({
                    valid: false,
                    message: "Error reading file for MIME validation",
                    mimeType: file.type
                });
            };
            
            // Read the first few bytes to determine file signature
            reader.readAsArrayBuffer(file.slice(0, 4));
        });
    },
    
    /**
     * Get file type from file signature (magic number)
     * @param {string} signature - Hex signature from file header
     * @returns {string|null} - MIME type or null if not recognized
     */
    getFileTypeFromSignature: function(signature) {
        // This is a simplified version - in production, you'd use a more comprehensive list
        switch (signature.toLowerCase()) {
            case "89504e47": return "image/png";            // PNG
            case "47494638": return "image/gif";            // GIF
            case "ffd8ffe0": 
            case "ffd8ffe1": 
            case "ffd8ffe2": 
            case "ffd8ffe3": return "image/jpeg";           // JPEG
            case "25504446": return "application/pdf";      // PDF
            case "504b0304": return "application/zip";      // ZIP (could also be DOCX, XLSX, etc. which are zip-based)
            case "d0cf11e0": return "application/msoffice"; // MS Office
            default: return null;
        }
    },
    
    /**
     * Check if the identified file type is consistent with the declared MIME type
     * @param {string} fileType - The identified file type from signature
     * @param {string} declaredType - The MIME type declared by the browser
     * @returns {boolean} - Whether the types are consistent
     */
    isMimeTypeConsistent: function(fileType, declaredType) {
        // Special case for Office files which can have various MIME types
        if (fileType === "application/msoffice") {
            return declaredType.includes("officedocument") || 
                   declaredType.includes("ms-excel") || 
                   declaredType.includes("ms-word") || 
                   declaredType.includes("ms-powerpoint");
        }
        
        // Special case for ZIP-based formats
        if (fileType === "application/zip") {
            return declaredType.includes("officedocument") || 
                   declaredType === "application/zip";
        }
        
        return fileType === declaredType;
    }
};