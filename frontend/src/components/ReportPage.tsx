import React, { useContext, useState } from 'react';
import { AuthContext, AuthContextData } from '../contexts/AuthContext';

const ReportPage: React.FC = () => {
  const { initialized, authenticated, login } = useContext<AuthContextData>(AuthContext);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reportDate, setReportDate] = useState<string>('2026-11-15');

  const downloadReport = async () => {
    if (!authenticated) {
      setError('Not authenticated');
      return;
    }

    try {
      setLoading(true);
      setError(null);

      const query = new URLSearchParams({ date: reportDate });
      const response = await fetch(`${process.env.REACT_APP_API_URL}/reports?${query.toString()}`, { credentials: 'include' });
      if (!response.ok) {
        throw new Error(`При получении отчёта произошла ошибка: ${response.status} ${response.statusText}`);
      }

      const data = await response.json();
      downloadJsonFile(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'An error occurred');
    } finally {
      setLoading(false);
    }
  };

  if (!initialized) {
    return <div>Loading...</div>;
  }

  if (!authenticated) {
    return (
      <div className="flex flex-col items-center justify-center min-h-screen bg-gray-100">
        <button
          onClick={login}
          className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600"
        >
          Login
        </button>
      </div>
    );
  }

  return (
    <div className="flex flex-col items-center justify-center min-h-screen bg-gray-100">
      <div className="p-8 bg-white rounded-lg shadow-md">
        <h1 className="text-2xl font-bold mb-6">Usage Reports</h1>

        <div className="mb-4">
          <label htmlFor="report-date" className="block text-sm font-medium text-gray-700 mb-1">
            Выберите дату отчёта<br></br>(данные есть с 14.11.2026 для prothetic1 и prothetic2)
          </label>
          <input
            id="report-date"
            type="date"
            value={reportDate}
            onChange={(e) => setReportDate(e.target.value)}
          />
        </div>
        
        <button
          onClick={downloadReport}
          disabled={loading || !reportDate}
          className={`px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 ${
            loading ? 'opacity-50 cursor-not-allowed' : ''
          }`}
        >
          {loading ? 'Generating Report...' : 'Download Report'}
        </button>

        {error && (
          <div className="mt-4 p-4 bg-red-100 text-red-700 rounded">
            {error}
          </div>
        )}
      </div>
    </div>
  );
};

export default ReportPage;

function downloadJsonFile(data: unknown){
  const obj = new Blob([JSON.stringify(data, null, 4)], { type: 'application/json;charset=utf-8' })  
  const url = window.URL.createObjectURL(obj);
  const link = document.createElement('a');
  link.href = url;
  link.setAttribute('download', 'report.json');
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => window.URL.revokeObjectURL(url), 200);
}