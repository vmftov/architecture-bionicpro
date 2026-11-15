import React from "react";
import ReportPage from "./components/ReportPage";
import { AuthContextProvider } from "./components/AuthContextProvider";

const App: React.FC = () => {
    return (
        <AuthContextProvider apiUri={process.env.REACT_APP_API_URL || ""}>
            <div className="App">
                <ReportPage />
            </div>
        </AuthContextProvider>
    );
};

export default App;
