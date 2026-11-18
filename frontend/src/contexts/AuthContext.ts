import React from "react";

export interface AuthContextData {
    initialized: boolean;
    authenticated: boolean;
    login: () => Promise<void>;
}

const defaultValue: AuthContextData = {
    initialized: false,
    authenticated: false,
    login: () => Promise.reject("Not implemented"),
};

export const AuthContext = React.createContext<AuthContextData>(defaultValue);
