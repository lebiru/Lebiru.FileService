# File Storage Service

File Storage Service is a simple ASP.NET Core application that allows users to upload, download, and manage files. It provides a RESTful API for file operations and includes a web interface for easy interaction.

## Features

- **File Upload**: Users can upload files to the server.
- **File Download**: Users can download files from the server.
- **File Listing**: Users can view a list of uploaded files along with their upload times.
- **Image Preview**: 🖼️ Image files are displayed with a preview in the web interface.
- **Text File Preview**: 📄 Text files show the first 100 characters as a preview in the web interface.

## Technologies Used

- **ASP.NET Core**: Backend framework for building web applications and APIs.
- **C#**: Programming language used in conjunction with ASP.NET Core.
- **HTML/CSS/JavaScript**: Frontend technologies for building the web interface.
- **Swagger**: API documentation tool used to document the RESTful API endpoints.
- **OpenShift**: Platform used for horizontal scaling and deployment of the application.

## Getting Started

To run the application locally, follow these steps:

1. **Clone this repository** to your local machine.
2. **Open the solution** in Visual Studio or your preferred IDE.
3. **Build the solution** to restore dependencies.
4. **Run the application** using the IDE's built-in tools or command-line interface.
5. **Access the application** through the provided URL (e.g., `http://localhost:port`).

## API Documentation

The API documentation is available through Swagger. Once the application is running, you can access the Swagger UI by navigating to `/swagger` in your browser.

## Usage

- **Uploading Files**: Use the provided web interface or send a POST request to `/File/CreateDoc` with the file attached as form data.
- **Downloading Files**: Click on the download link in the web interface or send a GET request to `/File/DownloadFile?filename=your_file_name` with the filename as a query parameter.
- **Listing Files**: View the list of uploaded files in the web interface or send a GET request to `/File/ListFiles`.
- **Viewing Image Previews**: Image previews are displayed automatically for image files in the web interface.

## Deployment

The application can be deployed to OpenShift for horizontal scaling and high availability. Configure your OpenShift environment and deploy the application using the provided deployment configurations.

## Contributing

Contributions are welcome! If you have any ideas, improvements, or bug fixes, feel free to open an issue or submit a pull request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

